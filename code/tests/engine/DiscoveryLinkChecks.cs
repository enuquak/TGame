using System.Net;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal sealed class DiscoveryLinkChecks(TestServices services)
{
    public async Task RunAsync(CancellationToken stop)
    {
        var configuration = TestNodes.LoadConfiguration();
        var first = new DiscoveryPeer(ServerProcess.FormatName(nameof(ProcessType.Router), 1), services.Ids);
        var second = new DiscoveryPeer(ServerProcess.FormatName(nameof(ProcessType.Router), 2), services.Ids);
        var third = new DiscoveryPeer(ServerProcess.FormatName(nameof(ProcessType.Router), 3), services.Ids);
        await using var firstApp = await StartAsync(first, stop);
        await using var secondApp = await StartAsync(second, stop);
        await using var thirdApp = await StartAsync(third, stop);
        var router1 = new DiscoveredProcess(new ServerProcess(ProcessType.Router, 1), firstApp.Urls.Single(), 1);
        var router2 = new DiscoveredProcess(new ServerProcess(ProcessType.Router, 2), secondApp.Urls.Single(), 2);
        var router3 = new DiscoveredProcess(new ServerProcess(ProcessType.Router, 3), thirdApp.Urls.Single(), 3);
        var unrelated = new DiscoveredProcess(new ServerProcess(ProcessType.Base, 1), "invalid-address", 1);
        var changes = Channel.CreateUnbounded<IReadOnlyList<DiscoveredProcess>>();
        var counts = System.Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
        counts[ProcessType.Router] = 2;
        var node = new ServerNode(new ServerProcess(ProcessType.Scene, 1), configuration, new ServerGroup(counts),
            services.ZooKeeper, services.Ids, services.Log);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var maintenance = node.MaintainConnectionsAsync(changes.Reader.ReadAllAsync(lifetime.Token), lifetime.Token);
        try
        {
            // 发现中的非主动目标必须被忽略，不能进入地址解析或连接。
            changes.Writer.TryWrite([unrelated, router1]);
            var old = await first.Connections.Reader.ReadAsync(stop);
            await old.Handshaken.Task.WaitAsync(stop);
            if (node.FirstLinks.IsCompleted || node.Ready)
            {
                throw new Exception("Discovery completed startup before two Routers were connected.");
            }

            changes.Writer.TryWrite([router1, router2]);
            var extra = await second.Connections.Reader.ReadAsync(stop);
            await extra.Handshaken.Task.WaitAsync(stop);
            await node.FirstLinks.WaitAsync(stop);
            changes.Writer.TryWrite([router1, router2, router3]);
            var late = await third.Connections.Reader.ReadAsync(stop);
            await late.Handshaken.Task.WaitAsync(stop);
            if (!node.Ready)
            {
                throw new Exception("Adding an extra Router revoked readiness.");
            }

            changes.Writer.TryWrite([router2]);
            await old.Closed.Task.WaitAsync(stop);
            await late.Closed.Task.WaitAsync(stop);
            if (!node.Ready)
            {
                throw new Exception("Removing a redundant Router revoked readiness.");
            }

            changes.Writer.TryWrite([router2 with { Generation = 3 }]);
            await extra.Closed.Task.WaitAsync(stop);
            var replacement = await second.Connections.Reader.ReadAsync(stop);
            await replacement.Handshaken.Task.WaitAsync(stop);
            changes.Writer.TryWrite([]);
            await replacement.Closed.Task.WaitAsync(stop);
            if (!node.Ready)
            {
                throw new Exception("Removing all discovered peers revoked completed startup readiness.");
            }

            changes.Writer.TryWrite([router1]);
            var restored = await first.Connections.Reader.ReadAsync(stop);
            await restored.Handshaken.Task.WaitAsync(stop);
            if (!node.Ready)
            {
                throw new Exception("Reconnecting a Router revoked readiness.");
            }

            changes.Writer.TryComplete(new InvalidDataException("Discovery failed."));
            try
            {
                await maintenance;
                throw new Exception("Discovery failure was hidden.");
            }
            catch (InvalidDataException)
            {
            }

            try
            {
                await node.CallPeerAsync<Empty>(router1.Process.Name, new Empty(), stop);
                throw new Exception("Discovery ended before its active connections were cleaned up.");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
            {
            }

            Console.WriteLine("PASS real gRPC startup minimum=2, late joins, removal below minimum, replacement, recovery and cleanup.");
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await maintenance;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (InvalidDataException) when (changes.Reader.Completion.IsFaulted)
            {
            }
        }
    }

    private static async Task<WebApplication> StartAsync(DiscoveryPeer peer, CancellationToken stop)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(peer);
        var app = builder.Build();
        app.MapGrpcService<DiscoveryPeer>();
        await app.StartAsync(stop);
        return app;
    }
}

internal sealed class DiscoveryPeer(string name, IdGenerator ids) : ServerLink.ServerLinkBase
{
    internal sealed class Connection
    {
        internal TaskCompletionSource Handshaken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal Channel<Connection> Connections { get; } = Channel.CreateUnbounded<Connection>();

    public override async Task Connect(IAsyncStreamReader<Frame> input, IServerStreamWriter<Frame> output, ServerCallContext context)
    {
        var connection = new Connection();
        try
        {
            if (!await input.MoveNext(context.CancellationToken) || input.Current.Hello != ServerProcess.FormatName(nameof(ProcessType.Scene), 1))
            {
                throw new InvalidDataException("Unexpected caller identity.");
            }

            await output.WriteAsync(new Frame { Hello = name }, context.CancellationToken);
            var requestId = ids.GenId();
            await output.WriteAsync(RpcFrames.Request(new Empty(), requestId), context.CancellationToken);
            Connections.Writer.TryWrite(connection);
            await foreach (var frame in input.ReadAllAsync(context.CancellationToken))
            {
                if (frame.Reply?.RequestId != requestId ||
                    frame.Reply.Error?.StatusCode != (int)StatusCode.Unimplemented)
                {
                    throw new InvalidDataException("Expected an unimplemented reply after the process handshake.");
                }

                connection.Handshaken.TrySetResult();
            }
        }
        finally
        {
            connection.Closed.TrySetResult();
        }
    }
}
