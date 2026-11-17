using System.Globalization;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame;
using TGame.Server.Engine;
using TGame.Transport;
using Enum = System.Enum;

internal static class EntityRoutingChecks
{
    public static async Task RunAsync(CancellationToken stop)
    {
        await CheckRoutingAsync(ProcessType.Login, stop);
        await CheckRoutingAsync(ProcessType.Base, stop);
        await CheckReadinessAsync(stop);
        await CheckSourceBoundaryAsync(stop);
        await CheckLocalIdentityCollisionAsync(stop);
        Console.WriteLine("PASS entity RPC: local, Gate relay, Router selection, nested calls, errors, cancellation and route removal.");
    }

    private static async Task CheckRoutingAsync(ProcessType entryType, CancellationToken stop)
    {
        await using var network = await Network.CreateAsync(entryType, stop);
        var gate = network.Node(ProcessType.Gate);
        var entry = network.Node(entryType);
        var first = network.Node(ProcessType.Profile);
        var second = network.Node(ProcessType.Profile, 2);

        await CheckReply(entry, entry, entry, "local");
        await CheckReply(gate, entry, gate, "direct");
        await CheckReply(gate, first, gate, "routed");
        await CheckReply(first, second, first, "peer");

        var nested = await gate.CallEntityAsync<StringValue>(first.State.EntityId, new StringValue { Value = "nested" }, stop);
        Check(nested.Value == $"{entry.Name}|{first.Name}|{entry.State.EntityId}|callback", "Nested routed request deadlocked or lost its origin.");

        var calls = Enumerable.Range(0, 24).Select(async index =>
        {
            var result = await gate.CallSinglePointAsync<StringValue>(ProcessType.Profile,
                new StringValue { Value = index.ToString(CultureInfo.InvariantCulture) }, stop);
            var fields = result.Value.Split('|');
            var target = fields[0] == first.Name ? first : second;
            Check(result.Value == $"{target.Name}|{gate.Name}|{target.State.EntityId}|{index}",
                "SinglePoint selection chose the wrong type/entity or correlated another call's reply.");
        });
        await Task.WhenAll(calls);

        await ExpectStatus(StatusCode.NotFound, () => gate.CallEntityAsync<StringValue>(network.StaleEntityId, new StringValue(), stop));
        await ExpectStatus(StatusCode.NotFound, () => gate.CallEntityAsync<StringValue>(NewId(), new StringValue(), stop));
        await ExpectStatus(StatusCode.Unavailable, () => gate.CallSinglePointAsync<StringValue>(ProcessType.Scene, new StringValue(), stop));
        await ExpectStatus(StatusCode.NotFound, () => entry.CallEntityAsync<StringValue>(gate.State.EntityId, new StringValue(), stop));
        await ExpectStatus(StatusCode.InvalidArgument, () => gate.CallSinglePointAsync<StringValue>(ProcessType.Gate, new StringValue(), stop));
        await ExpectStatus(StatusCode.Unimplemented, () => gate.CallEntityAsync<StringValue>(first.State.EntityId, new Empty(), stop));
        await ExpectStatus(StatusCode.Internal, () => gate.CallEntityAsync<StringValue>(first.State.EntityId, new StringValue { Value = "fail" }, stop));
        await CheckReply(gate, first, gate, "after-errors");

        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            network.ResetHold();
            var pending = gate.CallEntityAsync<StringValue>(first.State.EntityId, new StringValue { Value = "hold" }, cancellation.Token);
            await network.HoldStarted.Task.WaitAsync(stop);
            cancellation.Cancel();
            try
            {
                await pending;
                throw new Exception("Cancelling the caller did not cancel its wait.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            network.HoldRelease.SetResult();
        }

        await CheckReply(gate, second, gate, "after-cancel");
        network.ResetHold();
        var disconnected = gate.CallEntityAsync<StringValue>(first.State.EntityId, new StringValue { Value = "hold" }, stop);
        await network.HoldStarted.Task.WaitAsync(stop);
        await network.DisconnectProfileAsync();
        await ExpectStatus(StatusCode.Unavailable, () => disconnected);

        for (int index = 0; index < 8; index++)
        {
            var survivor = await gate.CallSinglePointAsync<StringValue>(ProcessType.Profile, new StringValue { Value = "survivor" }, stop);
            Check(survivor.Value == $"{second.Name}|{gate.Name}|{second.State.EntityId}|survivor", "Disconnected SinglePoint remained in the candidate index.");
        }

        await ExpectStatus(StatusCode.NotFound, () => gate.CallEntityAsync<StringValue>(first.State.EntityId, new StringValue(), stop));

        await network.ReconnectProfileAsync();
        await CheckReply(gate, first, gate, "reconnected");

        async Task CheckReply(ServerNode caller, ServerNode target, ServerNode source, string value)
        {
            var result = await caller.CallEntityAsync<StringValue>(target.State.EntityId, new StringValue { Value = value }, stop);
            Check(result.Value == $"{target.Name}|{source.Name}|{target.State.EntityId}|{value}", "Entity target, original caller or reply payload changed in transit.");
        }
    }

    private static async Task CheckReadinessAsync(CancellationToken stop)
    {
        await using var network = await Network.CreateAsync(ProcessType.Login, stop, initializeProfile: false);
        var gate = network.Node(ProcessType.Gate);
        var profile = network.Node(ProcessType.Profile);
        Check(!profile.State.LocalEntityIds.Any(), "Engine startup registered local entities before SinglePoint initialization.");
        Check(!network.Node(ProcessType.Router).IsEntityOwner(profile.Name, profile.State.EntityId),
            "Router registered an entity from the connection handshake.");
        await ExpectStatus(StatusCode.NotFound, () => gate.CallEntityAsync<StringValue>(profile.State.EntityId, new StringValue(), stop));
        await profile.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
        Check(network.Node(ProcessType.Router).IsEntityOwner(profile.Name, profile.State.EntityId),
            "SinglePoint initialization completed before Router acknowledged its entity.");
        var response = await gate.CallEntityAsync<StringValue>(profile.State.EntityId, new StringValue { Value = "ready" }, stop);
        Check(response.Value.EndsWith("|ready", StringComparison.Ordinal), "Initialized target did not accept its next request.");
    }

    private static async Task CheckSourceBoundaryAsync(CancellationToken stop)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Gate] = 1;
        counts[ProcessType.Login] = 1;
        var group = new ServerGroup(counts);
        var dispatcher = new RpcDispatcher();
        var gate = ServerProcess.FormatName(nameof(ProcessType.Gate), 1);
        var login = ServerProcess.FormatName(nameof(ProcessType.Login), 1);
        var expiredHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.RegisterEntity<Empty, StringValue>((context, _, _) => Task.FromResult(new StringValue { Value = context.SourceProcess }));
        dispatcher.RegisterEntity<StringValue, StringValue>(async (_, request, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return request;
            }
            finally
            {
                expiredHandler.TrySetResult();
            }
        });
        using var node = new ServerNode(login, group, dispatcher, TestIds.Generator);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var stream = new FrameStream();
        stream.Input.Writer.TryWrite(TestNodes.Hello(gate));
        var running = node.RunLinkAsync(gate, NewId(), stream, stream, lifetime.Token);
        try
        {
            await stream.Output.Reader.ReadAsync(stop);
            await node.FirstLinks.WaitAsync(stop);
            await node.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
            var forged = new RpcRequest
            {
                RequestId = NewId(), SourceProcess = login,
                DeadlineUnixMs = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(),
                EntityId = node.State.EntityId, Payload = Any.Pack(new Empty())
            };
            stream.Input.Writer.TryWrite(new Frame { Request = forged });
            var rejected = (await stream.Output.Reader.ReadAsync(stop)).Reply;
            Check(rejected.RequestId == forged.RequestId && rejected.Error?.StatusCode == (int)StatusCode.InvalidArgument,
                "Gate was allowed to forge another process's RPC source.");

            var valid = forged.Clone();
            valid.RequestId = NewId();
            valid.SourceProcess = gate;
            stream.Input.Writer.TryWrite(new Frame { Request = valid });
            var response = (await stream.Output.Reader.ReadAsync(stop)).Reply;
            Check(response.Payload.Unpack<StringValue>().Value == gate, "Rejected source broke the shared connection.");

            var expiring = valid.Clone();
            expiring.RequestId = NewId();
            expiring.Payload = Any.Pack(new StringValue());
            expiring.DeadlineUnixMs = DateTimeOffset.UtcNow.AddMilliseconds(100).ToUnixTimeMilliseconds();
            stream.Input.Writer.TryWrite(new Frame { Request = expiring });
            var expired = (await stream.Output.Reader.ReadAsync(stop)).Reply;
            Check(expired.Error?.StatusCode == (int)StatusCode.DeadlineExceeded, "Remote handler did not respect the request deadline.");
            await expiredHandler.Task.WaitAsync(stop);

            valid.RequestId = NewId();
            stream.Input.Writer.TryWrite(new Frame { Request = valid });
            var afterDeadline = (await stream.Output.Reader.ReadAsync(stop)).Reply;
            Check(afterDeadline.Payload.Unpack<StringValue>().Value == gate, "Expired handler prevented the next request.");
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await running;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task CheckLocalIdentityCollisionAsync(CancellationToken stop)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Router] = 1;
        counts[ProcessType.Profile] = 1;
        var group = new ServerGroup(counts);
        var profile = ServerProcess.FormatName(nameof(ProcessType.Profile), 1);
        using var router = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Router), 1), group, new RpcDispatcher(), TestIds.Generator);
        var stream = new FrameStream();
        stream.Input.Writer.TryWrite(TestNodes.Hello(profile));
        var running = router.RunLinkAsync(profile, NewId(), stream, stream, stop);
        await stream.Output.Reader.ReadAsync(stop);
        try
        {
            await ExpectStatus(StatusCode.AlreadyExists, () => TestNodes.RegisterAsync(stream, [router.State.EntityId], stop));
        }
        finally
        {
            stream.Input.Writer.Complete();
            await running.WaitAsync(stop);
        }
    }

    private static async Task ExpectStatus(StatusCode status, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RpcException error) when (error.StatusCode == status)
        {
            return;
        }

        throw new Exception($"Expected routed RPC status {status}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static string NewId() => TestIds.Generator.GenId();

    private sealed class Network : IAsyncDisposable
    {
        private readonly Dictionary<string, ServerNode> nodes = [];
        private readonly List<Connection> connections = [];
        private readonly CancellationToken stop;
        private readonly ProcessType entryType;

        private Network(ProcessType entryType, CancellationToken stop)
        {
            this.stop = stop;
            this.entryType = entryType;
        }

        public string StaleEntityId { get; } = NewId();
        public TaskCompletionSource HoldStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HoldRelease { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServerNode Node(ProcessType type, int number = 1) => nodes[ServerProcess.FormatName(type.ToString(), number)];

        public static async Task<Network> CreateAsync(ProcessType entryType, CancellationToken stop, bool initializeProfile = true)
        {
            var network = new Network(entryType, stop);
            try
            {
                await network.StartAsync(initializeProfile);
                return network;
            }
            catch
            {
                await network.DisposeAsync();
                throw;
            }
        }

        private async Task StartAsync(bool initializeProfile)
        {
            var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
            counts[ProcessType.Gate] = 1;
            counts[entryType] = 1;
            counts[ProcessType.Router] = 1;
            counts[ProcessType.Profile] = 2;
            var group = new ServerGroup(counts);

            foreach (var process in group.Processes)
            {
                var dispatcher = new RpcDispatcher();
                var node = new ServerNode(process.Name, group, dispatcher, TestIds.Generator);
                nodes.Add(process.Name, node);
                dispatcher.RegisterEntity<StringValue, StringValue>(async (context, request, token) =>
                {
                    if (request.Value == "nested")
                    {
                        return await node.CallEntityAsync<StringValue>(Node(entryType).State.EntityId, new StringValue { Value = "callback" }, token);
                    }

                    if (request.Value == "hold")
                    {
                        var release = HoldRelease;
                        HoldStarted.TrySetResult();
                        await release.Task.WaitAsync(token);
                    }

                    if (request.Value == "fail")
                    {
                        throw new InvalidOperationException("Test handler failed.");
                    }

                    return new StringValue { Value = $"{node.Name}|{context.SourceProcess}|{context.EntityId}|{request.Value}" };
                });
            }

            foreach (var caller in nodes.Values)
            {
                foreach (var peer in Topology.DialTargets(group, caller.Name))
                {
                    var connection = new Connection(stop);
                    connections.Add(connection);
                    var receiver = nodes[peer];
                    if (caller == Node(ProcessType.Profile) && receiver.Role == nameof(ProcessType.Router))
                    {
                        connection.Caller.ExtraRegistrationEntity = StaleEntityId;
                    }

                    await connection.Caller.WriteAsync(TestNodes.Hello(caller.Name), stop);
                    connection.Name = caller.Name;
                    connection.Running = Task.WhenAll(
                        receiver.RunLinkAsync(caller.Name, NewId(), connection.Receiver, connection.Receiver, connection.Stop),
                        caller.RunLinkAsync(receiver.Name, NewId(), connection.Caller, connection.Caller, connection.Stop));
                }
            }

            foreach (var node in nodes.Values)
            {
                await node.FirstLinks.WaitAsync(stop);
                if (initializeProfile || node != Node(ProcessType.Profile))
                {
                    await node.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
                }
            }
        }

        public void ResetHold()
        {
            HoldStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            HoldRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task DisconnectProfileAsync() => connections.Single(connection => connection.Name == Node(ProcessType.Profile).Name).StopAsync();

        public async Task ReconnectProfileAsync()
        {
            var profile = Node(ProcessType.Profile);
            var router = Node(ProcessType.Router);
            var connection = new Connection(stop) { Name = profile.Name };
            connections.Add(connection);
            await connection.Caller.WriteAsync(TestNodes.Hello(profile.Name), stop);
            connection.Running = Task.WhenAll(
                router.RunLinkAsync(profile.Name, NewId(), connection.Receiver, connection.Receiver, connection.Stop),
                profile.RunLinkAsync(router.Name, NewId(), connection.Caller, connection.Caller, connection.Stop));

            const int RegistrationPollMilliseconds = 10;
            while (!profile.Ready || !router.Ready)
            {
                await Task.Delay(RegistrationPollMilliseconds, stop);
            }

            Check(router.IsEntityOwner(profile.Name, profile.State.EntityId), "Reconnected SinglePoint did not restore its entity route.");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in connections)
            {
                await connection.StopAsync();
            }

            foreach (var node in nodes.Values)
            {
                node.Dispose();
            }
        }
    }

    private sealed class Connection
    {
        private readonly CancellationTokenSource lifetime;
        public WireStream Caller { get; }
        public WireStream Receiver { get; }
        public string Name { get; set; } = "";
        public Task Running { get; set; } = Task.CompletedTask;
        public CancellationToken Stop => lifetime.Token;

        public Connection(CancellationToken stop)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var outgoing = Channel.CreateUnbounded<Frame>();
            var incoming = Channel.CreateUnbounded<Frame>();
            Caller = new WireStream(incoming, outgoing);
            Receiver = new WireStream(outgoing, incoming);
        }

        public async Task StopAsync()
        {
            lifetime.Cancel();
            try
            {
                await Running;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private sealed class WireStream(Channel<Frame> input, Channel<Frame> output) : IAsyncStreamReader<Frame>, IAsyncStreamWriter<Frame>
    {
        public Frame Current { get; private set; } = null!;
        public WriteOptions? WriteOptions { get; set; }
        public string? ExtraRegistrationEntity { get; set; }
        public Task WriteAsync(Frame frame) => WriteAsync(frame, CancellationToken.None);
        public Task WriteAsync(Frame frame, CancellationToken stop)
        {
            var copy = Frame.Parser.ParseFrom(frame.ToByteArray());
            if (ExtraRegistrationEntity is not null && copy.Request?.Payload?.Is(RegisterEntitiesRequest.Descriptor) == true)
            {
                var registration = copy.Request.Payload.Unpack<RegisterEntitiesRequest>();
                registration.EntityIds.Add(ExtraRegistrationEntity);
                copy.Request.Payload = Any.Pack(registration);
            }

            return output.Writer.WriteAsync(copy, stop).AsTask();
        }

        public async Task<bool> MoveNext(CancellationToken stop)
        {
            if (!await input.Reader.WaitToReadAsync(stop))
            {
                return false;
            }

            Current = await input.Reader.ReadAsync(stop);
            return true;
        }
    }
}
