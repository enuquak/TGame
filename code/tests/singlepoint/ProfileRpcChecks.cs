using Any = Google.Protobuf.WellKnownTypes.Any;
using TGame;
using TGame.Profile;
using TGame.Server.Engine;
using TGame.Server.Logic;
using Grpc.Core;
using Empty = Google.Protobuf.WellKnownTypes.Empty;
using TGame.Transport;
using Result = TGame.Profile.ProfileReply.Types.Result;

internal static class ProfileRpcChecks
{
    public static async Task RunAsync(CancellationToken stop)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Router] = 1;
        counts[ProcessType.Base] = 1;
        counts[ProcessType.GM] = 1;
        counts[ProcessType.Profile] = 2;
        var group = new ServerGroup(counts);
        using var node = TestNodes.Create(ServerProcess.FormatName(nameof(ProcessType.Router), 1), group);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var peers = Topology.Peers(group, node.Name).ToDictionary(name => name, _ => new FrameStream());
        var entities = peers.Keys.ToDictionary(name => name, _ => NewId());
        var running = new List<Task>();
        foreach (var (peer, stream) in peers)
        {
            stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
            running.Add(node.RunLinkAsync(peer, NewId(), stream, stream, lifetime.Token));
            await stream.Output.Reader.ReadAsync(stop);
            await TestNodes.RegisterAsync(stream, [entities[peer]], stop);
        }

        try
        {
            await node.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
            var source = ServerProcess.FormatName(nameof(ProcessType.Base), 1);
            var forbidden = ServerProcess.FormatName(nameof(ProcessType.GM), 1);
            var profileProcesses = group.Processes.Where(process => process.Type == ProcessType.Profile);
            var profileNames = profileProcesses.Select(process => process.Name).ToArray();
            var profiles = profileNames.Select(name => peers[name]).ToArray();
            var owned = entities[source];
            var foreign = entities[profileNames[0]];

            await RejectedAsync(source, new ProfileRequest { RequestId = NewId(), Withdraw = foreign });
            await RejectedAsync(source, new ProfileRequest
            {
                RequestId = NewId(), Publish = new EntitySummary { EntityId = foreign, DisplayName = "foreign" }
            });
            await ForwardedAsync(new ProfileRequest { RequestId = NewId(), Fetch = owned }, owned, Result.Missing, forbidden);
            await RejectedAsync(source, new ProfileRequest { RequestId = NewId(), Fetch = "invalid-id" });
            await RejectedAsync(source, new ProfileRequest { RequestId = "invalid-id", Fetch = owned });
            await RejectedAsync(source, new ProfileRequest
            {
                RequestId = NewId(), Publish = new EntitySummary { EntityId = owned, DisplayName = " " }
            });
            await RejectedAsync(source, new ProfileRequest
            {
                RequestId = NewId(), Publish = new EntitySummary { EntityId = owned, DisplayName = new string('x', 81) }
            });
            await RejectedAsync(source, new ProfileRequest
            {
                RequestId = NewId(), Publish = new EntitySummary { EntityId = owned, DisplayName = "valid", Title = new string('x', 81) }
            });

            await RejectedAsync(source, new ProfileRequest
            {
                RequestId = NewId(), Publish = new EntitySummary { EntityId = owned, DisplayName = "owned" }
            });
            await ForwardedAsync(new ProfileRequest { RequestId = NewId(), Fetch = foreign }, foreign, Result.Found);
            await RejectedAsync(source, new ProfileRequest { RequestId = NewId(), Withdraw = owned });
            await RejectedAsync(source, new ProfileRequest { RequestId = NewId() });
            using (var point = new ProfileServerSinglePoint())
            {
                try
                {
                    await point.HandleRequestAsync(new EntityRpcContext(NewId(), source, point.EntityId), Any.Pack(new Empty()), stop);
                    throw new Exception("Profile accepted an unsupported RPC type.");
                }
                catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented)
                {
                }
            }

            async Task RejectedAsync(string from, ProfileRequest request)
            {
                using var point = new ProfileServerSinglePoint();
                var context = new EntityRpcContext(NewId(), from, point.EntityId);
                var reply = (ProfileReply)await point.HandleRequestAsync(context, Any.Pack(request), stop);
                if (reply.RequestId != request.RequestId || reply.Result != Result.Rejected)
                {
                    throw new Exception("Profile must reject invalid queries and writes in its own request entry.");
                }
            }

            async Task ForwardedAsync(ProfileRequest request, string subject, Result result, string? from = null)
            {
                from ??= source;
                var frame = Request(from, request);
                peers[from].Input.Writer.TryWrite(frame);
                await Task.WhenAny(profiles.Select(stream => stream.Output.Reader.WaitToReadAsync(stop).AsTask())).WaitAsync(stop);
                var index = Array.FindIndex(profiles, stream => stream.Output.Reader.TryPeek(out _));
                var forwarded = await profiles[index].Output.Reader.ReadAsync(stop);
                if (forwarded.Request.RequestId != frame.Request.RequestId ||
                    forwarded.Request.SourceProcess != from ||
                    forwarded.Request.EntityId != entities[profileNames[index]] ||
                    !forwarded.ReadRequest<ProfileRequest>().Equals(request))
                {
                    throw new Exception("Profile routing changed the selected target, source or payload.");
                }

                profiles[index].Input.Writer.TryWrite(RpcFrames.Reply(new ProfileReply
                {
                    RequestId = request.RequestId, EntityId = subject, Result = result
                }, forwarded.Request.RequestId));
                var response = await peers[from].Output.Reader.ReadAsync(stop);
                var reply = response.ReadReply<ProfileReply>();
                if (response.Reply.RequestId != frame.Request.RequestId || reply.RequestId != request.RequestId || reply.EntityId != subject || reply.Result != result)
                {
                    throw new Exception("Profile response did not return to the original caller.");
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await Task.WhenAll(running);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        Console.WriteLine("PASS Profile routing without business registration, read-only business validation and reply correlation.");
    }

    internal static Frame Request(string source, ProfileRequest request) => new()
    {
        Request = new RpcRequest
        {
            RequestId = NewId(), SourceProcess = source, SinglePointType = nameof(ProcessType.Profile),
            Payload = Any.Pack(request), DeadlineUnixMs = DateTimeOffset.UtcNow.Add(PeerLink.RequestTimeout).ToUnixTimeMilliseconds()
        }
    };

    private static string NewId() => IdGenerator.GenId();
}
