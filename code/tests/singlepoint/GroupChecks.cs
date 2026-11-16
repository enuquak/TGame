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
        await CheckBaseRoutersAsync(stop);

        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
        counts[ProcessType.Login] = 2;
        counts[ProcessType.Base] = 3;
        var group = new ServerGroup(counts);
        var point = new WaitingSinglePoint();
        var gate = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), point, group);
        var initialization = gate.InitializeAsync(stop);
        var streams = new Dictionary<string, FrameStream>();
        var tasks = new List<Task>();

        foreach (var peer in new[] { ServerProcess.FormatName(nameof(ProcessType.Login), 1), ServerProcess.FormatName(nameof(ProcessType.Base), 1) })
        {
            var stream = new FrameStream();
            streams.Add(peer, stream);
            tasks.Add(Open(peer, stream));
        }

        if (gate.FirstLinks.IsCompleted || point.Started.Task.IsCompleted)
        {
            throw new Exception("Gate became ready after only 1 Login and 1 Base; configuration requires 2 Login and 3 Base.");
        }

        await Rejected(ServerProcess.FormatName(nameof(ProcessType.Login), 1), StatusCode.AlreadyExists);
        await Rejected(ServerProcess.FormatName(nameof(ProcessType.Login), 3), StatusCode.InvalidArgument);
        await Rejected(ServerProcess.FormatName(nameof(ProcessType.Router), 1), StatusCode.InvalidArgument);

        foreach (var peer in group.Processes.Where(peer => peer.Number > 1 && peer.Type is ProcessType.Login or ProcessType.Base))
        {
            if (peer.Type == ProcessType.Base && peer.Number == 3)
            {
                var failed = new FrameStream();
                failed.Input.Writer.TryWrite(new Frame { Hello = ServerProcess.FormatName(nameof(ProcessType.Base), 1) });
                try
                {
                    await gate.RunLinkAsync(peer.Name, Guid.NewGuid().ToString("N"), [], failed, failed, stop);
                    throw new Exception("A handshake from a different instance was accepted.");
                }
                catch (IOException)
                {
                }

                if (gate.FirstLinks.IsCompleted || point.Started.Task.IsCompleted)
                {
                    throw new Exception("A missing Base instance was replaced by another peer or a failed handshake.");
                }
            }

            var stream = new FrameStream();
            streams.Add(peer.Name, stream);
            tasks.Add(Open(peer.Name, stream));
        }

        await gate.FirstLinks.WaitAsync(stop);
        await point.Started.Task.WaitAsync(stop);
        if (gate.Ready)
        {
            throw new Exception("Gate skipped role initialization after connecting all configured instances.");
        }

        point.Release.SetResult();
        await initialization.WaitAsync(stop);
        if (!gate.Ready)
        {
            throw new Exception("Gate did not become ready after all configured instances connected.");
        }

        var lostPeer = ServerProcess.FormatName(nameof(ProcessType.Base), 2);
        streams[lostPeer].Input.Writer.Complete();
        const int DisconnectPollMilliseconds = 10;
        while (gate.Ready)
        {
            await Task.Delay(DisconnectPollMilliseconds, stop);
        }

        if (!gate.FirstLinks.IsCompletedSuccessfully || !point.Initialized)
        {
            throw new Exception("Disconnect reset initialization instead of current readiness.");
        }

        var reconnected = new FrameStream();
        streams[lostPeer] = reconnected;
        tasks.Add(Open(lostPeer, reconnected));
        if (!gate.Ready)
        {
            throw new Exception("Readiness did not recover after the missing instance reconnected.");
        }

        foreach (var stream in streams.Values)
        {
            stream.Input.Writer.Complete();
        }

        await Task.WhenAll(tasks).WaitAsync(stop);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var cancelledPoint = new WaitingSinglePoint();
        var cancelledNode = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), cancelledPoint, group);
        var cancelledInit = cancelledNode.InitializeAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            await cancelledInit;
            throw new Exception("Waiting for configured peers ignored cancellation.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        if (cancelledPoint.Initialized || cancelledNode.FirstLinks.IsCompleted)
        {
            throw new Exception("Cancellation completed the first-links signal.");
        }

        var isolatedCounts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        isolatedCounts[ProcessType.Router] = 1;
        var isolated = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Router), 1), new RouterServerSinglePoint(), new ServerGroup(isolatedCounts));
        await isolated.InitializeAsync(stop);
        if (!isolated.Ready)
        {
            throw new Exception("A node with zero configured neighbors waited forever.");
        }

        Console.WriteLine("PASS Gate waits for all configured Login and Base instances.");
        await CheckRoutingAsync(group, stop);

        async Task Rejected(string peer, StatusCode expected)
        {
            try
            {
                await Open(peer, new FrameStream());
                throw new Exception($"Accepted invalid peer {peer}.");
            }
            catch (RpcException error) when (error.StatusCode == expected)
            {
            }
        }

        Task Open(string peer, FrameStream stream)
        {
            stream.Input.Writer.TryWrite(new Frame { Hello = peer });
            return gate.RunLinkAsync(peer, Guid.NewGuid().ToString("N"), [], stream, stream, stop);
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
            group.GetProcess(nameof(ProcessType.Base) + "1").Number != 1 ||
            group.Contains(nameof(ProcessType.Router)))
        {
            throw new Exception("Every instance must include its number, including a single instance and instance 1.");
        }

        Console.WriteLine("PASS explicit instance numbering starts at 1 for every process type.");
    }

    private static async Task CheckBaseRoutersAsync(CancellationToken stop)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
        counts[ProcessType.Router] = 2;
        counts[ProcessType.Base] = 2;
        counts[ProcessType.Gate] = 2;
        counts[ProcessType.DBMgr] = 2;

        var group = new ServerGroup(counts);
        var point = new WaitingSinglePoint();
        var name = ServerProcess.FormatName(nameof(ProcessType.Base), 2);
        var missing = ServerProcess.FormatName(nameof(ProcessType.Router), 2);
        var node = new GameServerNode(name, point, group);
        var initialization = node.InitializeAsync(stop);
        var peers = Topology.Peers(group, name);
        var streams = new Dictionary<string, FrameStream>();
        var tasks = new Dictionary<string, Task>();

        if (peers.Count != 6 ||
            Topology.DialTargets(group, name).Length != 4 ||
            !node.AcceptsIncoming(ServerProcess.FormatName(nameof(ProcessType.Gate), 2)) ||
            node.AcceptsIncoming(missing))
        {
            throw new Exception("Base topology did not expand incoming Gates and outgoing Routers/DBMgrs by instance.");
        }

        foreach (var peer in peers)
        {
            if (peer == missing)
            {
                continue;
            }

            Open(peer);
        }

        if (node.FirstLinks.IsCompleted || point.Started.Task.IsCompleted || node.Ready)
        {
            throw new Exception("Base initialized with the second configured Router still missing.");
        }

        Open(missing);
        await node.FirstLinks.WaitAsync(stop);
        await point.Started.Task.WaitAsync(stop);
        if (node.Ready)
        {
            throw new Exception("Topology readiness skipped Base business initialization.");
        }

        point.Release.SetResult();
        await initialization.WaitAsync(stop);
        if (!node.Ready)
        {
            throw new Exception("Base did not become ready with both Routers and every other expected peer.");
        }

        streams[missing].Input.Writer.Complete();
        await tasks[missing].WaitAsync(stop);
        if (node.Ready || !node.FirstLinks.IsCompletedSuccessfully)
        {
            throw new Exception("Losing Router2 did not clear readiness while preserving the first-links signal.");
        }

        Open(missing);
        if (!node.Ready)
        {
            throw new Exception("Base did not recover when Router2 reconnected.");
        }

        foreach (var stream in streams.Values)
        {
            stream.Input.Writer.Complete();
        }

        await Task.WhenAll(tasks.Values).WaitAsync(stop);
        Console.WriteLine("PASS Base2 waits for both Routers and all incoming/outgoing peers; Router2 loss and recovery.");

        void Open(string peer)
        {
            var stream = new FrameStream();
            streams[peer] = stream;
            stream.Input.Writer.TryWrite(new Frame { Hello = peer });
            tasks[peer] = node.RunLinkAsync(peer, Guid.NewGuid().ToString("N"), [], stream, stream, stop);
        }
    }

    private static async Task CheckRoutingAsync(ServerGroup group, CancellationToken stop)
    {
        var router = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Router), 1), new RouterServerSinglePoint(), group);
        var initialization = router.InitializeAsync(stop);
        var streams = new Dictionary<string, FrameStream>();
        var ids = new Dictionary<string, string>();
        var tasks = new List<Task>();
        foreach (var peer in Topology.Peers(group, router.Name))
        {
            var stream = new FrameStream();
            var entity = Guid.NewGuid().ToString("N");
            streams.Add(peer, stream);
            ids.Add(peer, entity);
            stream.Input.Writer.TryWrite(new Frame { Hello = peer });
            tasks.Add(router.RunLinkAsync(peer, Guid.NewGuid().ToString("N"), [entity], stream, stream, stop));
            await stream.Output.Reader.ReadAsync(stop);
        }

        await initialization.WaitAsync(stop);
        var secondBase = ServerProcess.FormatName(nameof(ProcessType.Base), 2);
        var publisher = streams[secondBase];
        var profile = streams[ServerProcess.FormatName(nameof(ProcessType.Profile), 1)];
        var request = new ProfileRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Publish = new EntitySummary { EntityId = ids[secondBase], DisplayName = "Group check", Title = "Owner" }
        };
        publisher.Input.Writer.TryWrite(new Frame { ProfileRequest = request });
        var forwarded = await profile.Output.Reader.ReadAsync(stop);
        if (forwarded.ProfileRequest?.Publish?.EntityId != ids[secondBase])
        {
            throw new Exception("Router lost the entity owner of the second Base instance.");
        }

        profile.Input.Writer.TryWrite(new Frame
        {
            ProfileReply = new ProfileReply
            {
                RequestId = request.RequestId,
                EntityId = ids[secondBase],
                Result = ProfileReply.Types.Result.Stored
            }
        });
        if ((await publisher.Output.Reader.ReadAsync(stop)).ProfileReply?.Result != ProfileReply.Types.Result.Stored)
        {
            throw new Exception("Profile reply did not return to the second Base instance.");
        }

        var firstBase = streams[ServerProcess.FormatName(nameof(ProcessType.Base), 1)];
        var foreign = request.Clone();
        foreign.RequestId = Guid.NewGuid().ToString("N");
        firstBase.Input.Writer.TryWrite(new Frame { ProfileRequest = foreign });
        if ((await firstBase.Output.Reader.ReadAsync(stop)).ProfileReply?.Result != ProfileReply.Types.Result.Rejected ||
            profile.Output.Reader.TryRead(out _))
        {
            throw new Exception("One Base instance was allowed to publish another Base instance's entity.");
        }

        var thirdBase = ServerProcess.FormatName(nameof(ProcessType.Base), 3);
        var caller = streams[ServerProcess.FormatName(nameof(ProcessType.Login), 2)];
        var receiver = streams[thirdBase];
        var ping = new TGame.Diagnostics.Ping
        {
            RequestId = Guid.NewGuid().ToString("N"), EntityId = ids[thirdBase], Entry = nameof(ProcessType.Login)
        };
        caller.Input.Writer.TryWrite(new Frame { Ping = ping });
        if ((await receiver.Output.Reader.ReadAsync(stop)).Ping?.RequestId != ping.RequestId)
        {
            throw new Exception("Router did not deliver to the exact third Base instance.");
        }

        receiver.Input.Writer.TryWrite(new Frame
        {
            Pong = new TGame.Diagnostics.Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Responder = nameof(ProcessType.Base) }
        });
        if ((await caller.Output.Reader.ReadAsync(stop)).Pong?.RequestId != ping.RequestId)
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
