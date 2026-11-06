using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using MongoDB.Bson;
using MongoDB.Driver;
using TGame.Server.Logic;
using TGame.Profile;

namespace TGame.PingPong;

internal sealed class LinkService(ProbeNode node) : ProbeLink.ProbeLinkBase
{
    public override Task Connect(IAsyncStreamReader<Frame> input, IServerStreamWriter<Frame> output, ServerCallContext context)
    {
        var peer = context.RequestHeaders.GetValue("peer") ?? "";
        var link = context.RequestHeaders.GetValue("link") ?? "";
        if (!Guid.TryParseExact(link, "N", out _) || !Topology.Dials.TryGetValue(peer, out var targets)
            || !targets.Contains(node.Role))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Unexpected link."));
        var entities = context.RequestHeaders.GetValue("entity")?.Split(',') ?? [];
        return node.RunLinkAsync(peer, link, entities, input, output, context.CancellationToken);
    }
}

internal sealed partial class ProbeNode(string role, ServerSinglePoint singlePoint, ProfileStore? profileStore)
{
    private readonly ConcurrentDictionary<string, PeerLink> links = new();
    private readonly ConcurrentDictionary<string, string> owners = new();
    private readonly ConcurrentDictionary<string, byte> localEntities = new();
    public string Role => role;
    public int LinkCount => links.Count;
    public bool Ready { get; set; }

    public List<Task> Start(IReadOnlyDictionary<string, string> addresses, CancellationToken stop)
    {
        List<Task> connections = [];
        void Connect(string peer) => connections.Add(ConnectAsync(peer, addresses[peer], stop));
        void Register(string id)
        {
            if (!localEntities.TryAdd(id, 0)) throw new InvalidOperationException($"Duplicate local entity {id}.");
        }

        if (Topology.Entities.TryGetValue(role, out var probeEntity)) Register(probeEntity);
        singlePoint.Initialize(Register, Topology.Dials[role], Connect);
        TraceLog.Event("SINGLE_POINT_INIT", role, entity: singlePoint.State.EntityId, detail: singlePoint.GetType().Name);
        return connections;
    }

    private async Task ConnectAsync(string peer, string address, CancellationToken stop)
    {
        using var channel = GrpcChannel.ForAddress(address);
        var id = Guid.NewGuid().ToString("N");
        var headers = new Metadata { { "peer", role }, { "link", id } };
        if (peer == "Router") headers.Add("entity", string.Join(',', localEntities.Keys));
        using var call = new ProbeLink.ProbeLinkClient(channel).Connect(headers, cancellationToken: stop);
        // Flush request headers before the first application message on an idle stream.
        await call.RequestStream.WriteAsync(new Frame { Hello = role }, stop);
        await RunLinkAsync(peer, id, [], call.ResponseStream, call.RequestStream, stop);
    }

    public async Task RunLinkAsync(string peer, string id, string[] entities, IAsyncStreamReader<Frame> input,
        IAsyncStreamWriter<Frame> output, CancellationToken stop)
    {
        var link = new PeerLink(role, peer, id, output);
        var probeEntity = Topology.Entities.GetValueOrDefault(peer);
        if (role == "Router" && (entities.Length == 0 || (probeEntity != null && !entities.Contains(probeEntity)) ||
            entities.Any(entity => entity != probeEntity && !Guid.TryParseExact(entity, "N", out _))))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid entity registration from {peer}."));
        if (!links.TryAdd(peer, link)) throw new InvalidDataException($"Duplicate link to {peer}.");
        List<string> registered = [];
        try
        {
            if (role == "Router")
                foreach (var entity in entities)
                {
                    if (!owners.TryAdd(entity, peer)) throw new InvalidDataException($"Duplicate entity {entity}.");
                    registered.Add(entity);
                    TraceLog.Event("ENTITY_ROUTE", role, peer, entity: entity, link: id);
                }
            TraceLog.Event("LINK_UP", role, peer, link: id);
            // A Scene handler can call Profile through the same Router stream while
            // handling an incoming ping. Keep receiving replies during that call.
            using var transport = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var queue = Channel.CreateBounded<Frame>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
            var read = ReadAsync();
            var serve = ServeAsync();
            await Task.WhenAny(read, serve);
            transport.Cancel();
            queue.Writer.TryComplete();
            link.Close();
            await Task.WhenAll(read, serve);

            async Task ReadAsync()
            {
                await foreach (var frame in input.ReadAllAsync(transport.Token))
                {
                    switch (frame.BodyCase)
                    {
                        case Frame.BodyOneofCase.Hello when frame.Hello == peer: break;
                        case Frame.BodyOneofCase.Pong: link.Receive(frame, frame.Pong.RequestId, frame.Pong.EntityId, "PONG_RX"); break;
                        case Frame.BodyOneofCase.ProfileReply:
                            link.Receive(frame, frame.ProfileReply.RequestId, frame.ProfileReply.EntityId, "PROFILE_REPLY_RX"); break;
                        case Frame.BodyOneofCase.Ping or Frame.BodyOneofCase.ProfileRequest:
                            if (!queue.Writer.TryWrite(frame)) throw new RpcException(new Status(StatusCode.ResourceExhausted, "Peer request queue is full."));
                            break;
                        default: throw new InvalidDataException("Invalid stream frame.");
                    }
                }
            }
            async Task ServeAsync()
            {
                await foreach (var frame in queue.Reader.ReadAllAsync(transport.Token))
                {
                    if (frame.ProfileRequest is { } request)
                    {
                        TraceLog.Event("PROFILE_REQUEST_RX", role, peer, request.RequestId, ProfileStore.Subject(request), id);
                        await link.ReplyAsync(await RouteProfileAsync(peer, request, transport.Token), transport.Token);
                        continue;
                    }
                    var ping = frame.Ping;
                    TraceLog.Event("PING_RX", role, peer, ping.RequestId, ping.EntityId, id);
                    try { await link.ReplyAsync(await RouteAsync(peer, ping, transport.Token), transport.Token); }
                    catch (Exception error) when (!transport.IsCancellationRequested)
                    {
                        TraceLog.Event("PING_FAILED", role, peer, ping.RequestId, ping.EntityId, id, error.Message);
                        throw;
                    }
                }
            }
        }
        finally
        {
            foreach (var entity in registered) owners.TryRemove(entity, out _);
            links.TryRemove(peer, out _);
            link.Close();
        }
    }

    private async Task<Pong> RouteAsync(string from, Ping ping, CancellationToken stop)
    {
        if (ping.Entry == "GM") return await RouteGmPingAsync(from, ping, stop);
        var gateLocal = ping.Entry == "Gate" && role == "Gate" && ping.EntityId == singlePoint.State.EntityId;
        if (!Guid.TryParseExact(ping.RequestId, "N", out _) || (!gateLocal && ping.Entry is not ("Base" or "Login"))
            || !(Topology.Entities.ContainsValue(ping.EntityId) || Guid.TryParseExact(ping.EntityId, "N", out _))
            || (ping.Storage && (ping.EntityId != Topology.Entities["DBMgr"] || ping.Entry != "Base")))
            throw new InvalidDataException("Invalid ping.");

        if (localEntities.ContainsKey(ping.EntityId))
        {
            if (ping.Storage)
            {
                if (from != "Base") throw new InvalidDataException("Storage probe did not use the dedicated link.");
                await PingMongoAsync(stop);
                TraceLog.Event("MONGO_PONG", role, "MongoDB", ping.RequestId, ping.EntityId);
            }
            var result = new Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Responder = role };
            if (ping.ProfileProbe != Ping.Types.ProfileProbe.None)
                result.ProfileReply = await RunProfileProbeAsync(ping, stop);
            return result;
        }
        string target = role switch
        {
            "Gate" when from == "Client" => ping.Entry,
            "Base" when from == "Gate" && ping.Storage => "DBMgr",
            "Base" or "Login" when from == "Gate" => "Router",
            "Router" when !ping.Storage && owners.TryGetValue(ping.EntityId, out var owner) && owner != from => owner,
            _ => throw new InvalidDataException($"No route from {from} through {role} to {ping.EntityId}.")
        };
        return await links[target].PingAsync(ping, stop);
    }

    private IMongoDatabase? database;
    private async Task PingMongoAsync(CancellationToken stop)
    {
        if (database == null)
        {
            database = TestStorage.Mongo(TestStorage.Settings.Value.DbMgrMongo).GetDatabase("tgame_test");
        }
        var reply = await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: stop);
        if (reply["ok"].ToDouble() != 1) throw new IOException("MongoDB ping failed.");
    }

    public async Task WebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest || !Ready) { context.Response.StatusCode = 503; return; }
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        string link = context.Connection.Id;
        while (await Wire.ReadAsync(ws, context.RequestAborted) is { } bytes)
        {
            var ping = Ping.Parser.ParseFrom(bytes);
            TraceLog.Event("PING_RX", role, "Client", ping.RequestId, ping.EntityId, link);
            var pong = await RouteAsync("Client", ping, context.RequestAborted);
            await ws.SendAsync(pong.ToByteArray().AsMemory(), WebSocketMessageType.Binary, true, context.RequestAborted);
            TraceLog.Event("PONG_TX", role, "Client", pong.RequestId, pong.EntityId, link);
        }
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
    }
}

internal sealed class PeerLink(string role, string peer, string id, IAsyncStreamWriter<Frame> output)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Frame>> waiting = new();
    private readonly SemaphoreSlim writer = new(1);

    private async Task WriteAsync(Frame frame, CancellationToken stop)
    {
        await writer.WaitAsync(stop);
        try { await output.WriteAsync(frame, stop); }
        finally { writer.Release(); }
    }
    public async Task<Pong> PingAsync(Ping ping, CancellationToken stop)
    {
        var frame = await CallAsync(new Frame { Ping = ping }, ping.RequestId, ping.EntityId, "PING_TX", stop);
        if (frame.Pong?.EntityId != ping.EntityId) throw new InvalidDataException("Pong entity mismatch.");
        return frame.Pong;
    }
    public async Task<ProfileReply> ProfileAsync(ProfileRequest request, CancellationToken stop)
    {
        var subject = ProfileStore.Subject(request);
        var frame = await CallAsync(new Frame { ProfileRequest = request }, request.RequestId, subject, "PROFILE_REQUEST_TX", stop);
        if (frame.ProfileReply?.EntityId != subject) throw new InvalidDataException("Profile reply entity mismatch.");
        return frame.ProfileReply;
    }
    private async Task<Frame> CallAsync(Frame frame, string requestId, string entity, string ev, CancellationToken stop)
    {
        var reply = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!waiting.TryAdd(requestId, reply)) throw new InvalidDataException("Duplicate request ID.");
        try
        {
            await WriteAsync(frame, stop);
            TraceLog.Event(ev, role, peer, requestId, entity, id);
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(12), stop);
        }
        finally { waiting.TryRemove(requestId, out _); }
    }
    public async Task ReplyAsync(Pong pong, CancellationToken stop)
    {
        await WriteAsync(new Frame { Pong = pong }, stop);
        TraceLog.Event("PONG_TX", role, peer, pong.RequestId, pong.EntityId, id);
    }
    public async Task ReplyAsync(ProfileReply reply, CancellationToken stop)
    {
        await WriteAsync(new Frame { ProfileReply = reply }, stop);
        TraceLog.Event("PROFILE_REPLY_TX", role, peer, reply.RequestId, reply.EntityId, id);
    }
    public void Receive(Frame frame, string requestId, string entity, string ev)
    {
        if (!waiting.TryGetValue(requestId, out var request))
        {
            // A timed-out call has already relinquished its slot on this shared stream.
            TraceLog.Event("UNMATCHED_REPLY", role, peer, requestId, entity, id);
            return;
        }
        TraceLog.Event(ev, role, peer, requestId, entity, id);
        request.SetResult(frame);
    }
    public void Close()
    {
        foreach (var pending in waiting.Values) pending.TrySetException(new IOException($"Link {role}-{peer} closed."));
    }
}
