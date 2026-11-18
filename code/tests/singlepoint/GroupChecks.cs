using StringValue = Google.Protobuf.WellKnownTypes.StringValue;
using Grpc.Core;
using TGame;
using TGame.Server.Engine;
using TGame.Server.Logic;
using TGame.Profile;
using TGame.Transport;

internal static class GroupChecks
{
    public static async Task RunAsync(CancellationToken stop)
    {
        CheckInstanceNames();
        await DynamicTopologyChecks.RunAsync(stop);
        await CheckIsolatedAsync(stop);
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
        counts[ProcessType.Login] = 2;
        counts[ProcessType.Base] = 3;
        var group = new ServerGroup(counts);
        using var configuration = TestNodes.InitializeConfiguration(group);
        await CheckRoutingAsync(group, stop);
    }

    private static async Task CheckIsolatedAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeIsolated(ProcessType.Router);
        using var isolated = TestNodes.Create(nameof(ProcessType.Router));
        await isolated.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
        if (!isolated.Ready)
        {
            throw new Exception("A node with zero configured neighbors waited forever.");
        }
    }

    private static void CheckInstanceNames()
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
        counts[ProcessType.Router] = 2;
        var group = new ServerGroup(counts);
        string[] expected = [nameof(ProcessType.Router) + "1", nameof(ProcessType.Router) + "2"];
        var routers = group.Processes.Where(process => process.Type == ProcessType.Router);
        if (!routers.Select(process => process.Name).SequenceEqual(expected) ||
            group.GetProcess(nameof(ProcessType.Base) + "1").ChannelId != 1 ||
            group.Contains(nameof(ProcessType.Router)))
        {
            throw new Exception("Every instance must include its number, including a single instance and instance 1.");
        }

        Console.WriteLine("PASS explicit instance numbering starts at 1 for every process type.");
    }

    private static async Task CheckRoutingAsync(ServerGroup group, CancellationToken stop)
    {
        using var router = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Router), 1));
        var streams = new Dictionary<string, FrameStream>();
        var ids = new Dictionary<string, string>();
        var tasks = new List<Task>();
        var registrations = new List<Task>();
        foreach (var peer in TestNodes.Peers(router.Name, group))
        {
            var stream = new FrameStream();
            var entity = IdGenerator.GenId();
            streams.Add(peer, stream);
            ids.Add(peer, entity);
            stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
            tasks.Add(router.RunLinkAsync(peer, IdGenerator.GenId(), stream, stream, stop));
            await stream.Output.Reader.ReadAsync(stop);
            registrations.Add(TestNodes.RegisterAsync(stream, [entity], stop));
        }

        await router.FirstLinks.WaitAsync(stop);
        await router.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
        await Task.WhenAll(registrations);
        var secondBase = ServerProcess.FormatName(nameof(ProcessType.Base), 2);
        var publisher = streams[secondBase];
        var profile = streams[ServerProcess.FormatName(nameof(ProcessType.Profile), 1)];
        var request = new ProfileRequest
        {
            RequestId = IdGenerator.GenId(),
            Fetch = ids[secondBase]
        };
        publisher.Input.Writer.TryWrite(ProfileRpcChecks.Request(secondBase, request));
        var forwarded = await profile.Output.Reader.ReadAsync(stop);
        if (forwarded.ReadRequest<ProfileRequest>().Fetch != ids[secondBase])
        {
            throw new Exception("Router lost the entity owner of the second Base instance.");
        }

        profile.Input.Writer.TryWrite(RpcFrames.Reply(new ProfileReply
        {
            RequestId = request.RequestId,
            EntityId = ids[secondBase],
            Result = ProfileReply.Types.Result.Missing
        }, forwarded.Request.RequestId));
        if ((await publisher.Output.Reader.ReadAsync(stop)).ReadReply<ProfileReply>().Result != ProfileReply.Types.Result.Missing)
        {
            throw new Exception("Profile reply did not return to the second Base instance.");
        }

        var firstBase = streams[ServerProcess.FormatName(nameof(ProcessType.Base), 1)];
        var foreign = request.Clone();
        foreign.RequestId = IdGenerator.GenId();
        firstBase.Input.Writer.TryWrite(ProfileRpcChecks.Request(ServerProcess.FormatName(nameof(ProcessType.Base), 1), foreign));
        var foreignQuery = await profile.Output.Reader.ReadAsync(stop);
        if (!foreignQuery.ReadRequest<ProfileRequest>().Equals(foreign))
        {
            throw new Exception("Router changed a query for another Base instance's entity.");
        }

        profile.Input.Writer.TryWrite(RpcFrames.Reply(new ProfileReply
        {
            RequestId = foreign.RequestId, EntityId = foreign.Fetch, Result = ProfileReply.Types.Result.Missing
        }, foreignQuery.Request.RequestId));
        if ((await firstBase.Output.Reader.ReadAsync(stop)).ReadReply<ProfileReply>().RequestId != foreign.RequestId)
        {
            throw new Exception("Query response did not return to the first Base instance.");
        }

        var thirdBase = ServerProcess.FormatName(nameof(ProcessType.Base), 3);
        var secondLogin = ServerProcess.FormatName(nameof(ProcessType.Login), 2);
        var caller = streams[secondLogin];
        var receiver = streams[thirdBase];
        var requestId = IdGenerator.GenId();
        var routed = RpcFrames.Request(new StringValue { Value = requestId }, requestId);
        routed.Request.SourceProcess = secondLogin;
        routed.Request.EntityId = ids[thirdBase];
        routed.Request.DeadlineUnixMs = DateTimeOffset.UtcNow.Add(PeerLink.RequestTimeout).ToUnixTimeMilliseconds();
        caller.Input.Writer.TryWrite(routed);
        var received = await receiver.Output.Reader.ReadAsync(stop);
        if (received.Request.RequestId != requestId || received.ReadRequest<StringValue>().Value != requestId)
        {
            throw new Exception("Router did not deliver to the exact third Base instance.");
        }

        receiver.Input.Writer.TryWrite(RpcFrames.Reply(new StringValue { Value = thirdBase }, requestId));
        var response = await caller.Output.Reader.ReadAsync(stop);
        if (response.Reply.RequestId != requestId || response.ReadReply<StringValue>().Value != thirdBase)
        {
            throw new Exception("Router did not return the reply to the second Login instance.");
        }

        foreach (var stream in streams.Values)
        {
            stream.Input.Writer.Complete();
        }

        await Task.WhenAll(tasks).WaitAsync(stop);
        Console.WriteLine("PASS multi-instance entity routing and ownership isolation.");
    }
}
