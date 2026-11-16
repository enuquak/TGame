using TGame.Diagnostics;
using TGame.Transport;
using TGame.Profile;
using System.Net.WebSockets;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TGame;
using TGame.Server;
using TGame.Server.Engine;
using TGame.Server.Logic;

const int TestTimeoutSeconds = 5;
const int HandshakeObservationMilliseconds = 200;
const string LegacyProbeEntityId = "body-1";

var engineAssembly = typeof(Topology).Assembly;
if (engineAssembly == typeof(ServerSinglePoint).Assembly)
{
    throw new Exception("Engine topology and business SinglePoint must be compiled into separate assemblies.");
}

Type[] engineTypes = [typeof(LinkService), typeof(ServerNode), typeof(PeerLink), typeof(ServiceRegistry)];
foreach (var type in engineTypes)
{
    if (type.Assembly != engineAssembly)
    {
        throw new Exception($"{type.Name} must belong to the engine assembly.");
    }
}

await MongoStorageChecks.RunAsync();

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));
var stop = deadline.Token;
var group = new ServerGroup(Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1));

await GroupChecks.RunAsync(stop);

foreach (var processType in Enum.GetValues<ProcessType>())
{
    var registeredIds = new List<string>();
    var singlePoint = new WaitingSinglePoint();
    singlePoint.Release.SetResult();

    await singlePoint.InitializeAsync(processType.ToString(), registeredIds.Add, Task.CompletedTask, stop);
    if (registeredIds.Count != 1 || registeredIds[0] != singlePoint.State.EntityId)
    {
        throw new Exception($"{processType} initialization registered IDs other than its own EntityId.");
    }
}

var loginNode = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Login), 1), new LoginServerSinglePoint(), group);
var loginInit = loginNode.InitializeAsync(stop);
var router = new FrameStream();
var routerLink = Open(loginNode, ServerProcess.FormatName(nameof(ProcessType.Router), 1), router);
var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var pendingGate = Open(loginNode, ServerProcess.FormatName(nameof(ProcessType.Gate), 1), new FrameStream { BeforeWrite = handshake.Task });
if (await Task.WhenAny(loginInit, Task.Delay(HandshakeObservationMilliseconds, stop)) == loginInit)
{
    Console.Error.WriteLine("Login became available before it sent its handshake reply.");
    return 1;
}
handshake.SetException(new IOException("Disconnected during handshake."));
try
{
    await pendingGate;
    throw new Exception("Failed handshake succeeded.");
}
catch (IOException)
{
}
if (loginNode.Ready)
{
    throw new Exception("Failed handshake counted as a connected peer.");
}

var retry = new FrameStream();
var accepted = Open(loginNode, ServerProcess.FormatName(nameof(ProcessType.Gate), 1), retry);
await loginInit.WaitAsync(stop);

var profileRequest = new ProfileRequest { RequestId = Guid.NewGuid().ToString("N"), Fetch = Guid.NewGuid().ToString("N") };
var profileCall = loginNode.CallProfileAsync(profileRequest, stop);
var profileFrame = await router.Output.Reader.ReadAsync(stop);
if (profileFrame.ProfileRequest?.RequestId != profileRequest.RequestId || profileFrame.ProfileRequest.Fetch != profileRequest.Fetch)
{
    throw new Exception("Profile requests must use their own transport message without a Ping wrapper.");
}

router.Input.Writer.TryWrite(new Frame
{
    ProfileReply = new ProfileReply
    {
        RequestId = profileRequest.RequestId,
        EntityId = profileRequest.Fetch,
        Result = ProfileReply.Types.Result.Missing
    }
});
if ((await profileCall.WaitAsync(stop)).Result != ProfileReply.Types.Result.Missing)
{
    throw new Exception("Profile reply was not delivered independently of Ping/Pong.");
}

retry.Input.Writer.Complete();
router.Input.Writer.Complete();
await Task.WhenAll(accepted, routerLink).WaitAsync(stop);

var point = new WaitingSinglePoint();
var gate = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), point, group);
var initialization = gate.InitializeAsync(stop);
if (point.Started.Task.IsCompleted)
{
    throw new Exception("Role initialization ran before the handshakes.");
}

var login = new FrameStream();
var basePeer = new FrameStream();
var streams = Task.WhenAll(Open(gate, ServerProcess.FormatName(nameof(ProcessType.Login), 1), login), Open(gate, ServerProcess.FormatName(nameof(ProcessType.Base), 1), basePeer));
await point.Started.Task.WaitAsync(stop);

var context = new DefaultHttpContext();
context.Features.Set<IHttpWebSocketFeature>(new WebSocketUpgrade());
await gate.WebSocketAsync(context);
if (context.Response.StatusCode != StatusCodes.Status503ServiceUnavailable)
{
    throw new Exception("Gate accepted traffic during role initialization.");
}

await ExpectPong(Pong.Types.Status.Unavailable);
point.Release.SetResult();
await initialization.WaitAsync(stop);
await ExpectPong(Pong.Types.Status.Ok);
login.Input.Writer.Complete();
basePeer.Input.Writer.Complete();
await streams.WaitAsync(stop);
if (gate.Ready)
{
    throw new Exception("Gate remained available after a peer disconnected.");
}

var cancelling = new WaitingSinglePoint();
using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
var pending = cancelling.InitializeAsync(nameof(ProcessType.Gate), _ =>
{
}, Task.CompletedTask, cancel.Token);
await cancelling.Started.Task.WaitAsync(stop);
cancel.Cancel();
try
{
    await pending.WaitAsync(stop);
    throw new Exception("Cancelled initialization completed successfully.");
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested && !stop.IsCancellationRequested)
{
}
if (cancelling.Initialized)
{
    throw new Exception("Cancelled SinglePoint became ready.");
}

var routes = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Router), 1), new RouterServerSinglePoint(), group);
var routerInit = routes.InitializeAsync(stop);
var duplicateEntityId = Guid.NewGuid().ToString("N");
string[][] invalidRegistrations = [[], [LegacyProbeEntityId], [duplicateEntityId, duplicateEntityId]];
foreach (var ids in invalidRegistrations)
{
    try
    {
        await Open(routes, ServerProcess.FormatName(nameof(ProcessType.Base), 1), new FrameStream(), ids).WaitAsync(stop);
        throw new Exception("Router accepted an empty, non-GUID or duplicate entity registration.");
    }
    catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
    {
    }
}

var neighbors = new Dictionary<string, FrameStream>();
var neighborIds = new Dictionary<string, string>();
foreach (var pair in Topology.Dials)
{
    if (!pair.Value.Contains(nameof(ProcessType.Router)))
    {
        continue;
    }

    var peer = ServerProcess.FormatName(pair.Key, 1);
    neighbors.Add(peer, new FrameStream());
    neighborIds.Add(peer, Guid.NewGuid().ToString("N"));
}

var routeTasks = new List<Task>();
foreach (var pair in neighbors)
{
    routeTasks.Add(Open(routes, pair.Key, pair.Value, [neighborIds[pair.Key]]));
}

var routing = Task.WhenAll(routeTasks);
await routerInit.WaitAsync(stop);
foreach (var stream in neighbors.Values)
{
    var reply = await stream.Output.Reader.ReadAsync(stop);
    if (reply.Hello != routes.Name)
    {
        throw new Exception("Router did not acknowledge a GUID-only registration.");
    }
}

var baseRoute = neighbors[ServerProcess.FormatName(nameof(ProcessType.Base), 1)];
var profileRoute = neighbors[ServerProcess.FormatName(nameof(ProcessType.Profile), 1)];
var publish = new ProfileRequest
{
    RequestId = Guid.NewGuid().ToString("N"),
    Publish = new EntitySummary
    {
        EntityId = neighborIds[ServerProcess.FormatName(nameof(ProcessType.Base), 1)],
        DisplayName = ProfileProbeData.DisplayName,
        Title = ProfileProbeData.Title
    }
};
baseRoute.Input.Writer.TryWrite(new Frame { ProfileRequest = publish });
var forwarded = await profileRoute.Output.Reader.ReadAsync(stop);
if (forwarded.ProfileRequest?.RequestId != publish.RequestId || forwarded.ProfileRequest.Publish?.EntityId != publish.Publish.EntityId)
{
    throw new Exception("Router did not forward an owned GUID entity to the Profile role.");
}

profileRoute.Input.Writer.TryWrite(new Frame
{
    ProfileReply = new ProfileReply
    {
        RequestId = publish.RequestId,
        EntityId = publish.Publish.EntityId,
        Result = ProfileReply.Types.Result.Stored
    }
});
var published = (await baseRoute.Output.Reader.ReadAsync(stop)).ProfileReply;
if (published?.RequestId != publish.RequestId || published.EntityId != publish.Publish.EntityId || published.Result != ProfileReply.Types.Result.Stored)
{
    throw new Exception("Profile reply did not return to the owner of the GUID entity.");
}

var foreignPublish = publish.Clone();
foreignPublish.RequestId = Guid.NewGuid().ToString("N");
foreignPublish.Publish.EntityId = neighborIds[ServerProcess.FormatName(nameof(ProcessType.Scene), 1)];
await ExpectProfileRejected(ServerProcess.FormatName(nameof(ProcessType.Base), 1), foreignPublish);
await ExpectProfileRejected(ServerProcess.FormatName(nameof(ProcessType.Base), 1), new ProfileRequest
{
    RequestId = Guid.NewGuid().ToString("N"),
    Withdraw = neighborIds[ServerProcess.FormatName(nameof(ProcessType.Scene), 1)]
});
await ExpectProfileRejected(ServerProcess.FormatName(nameof(ProcessType.Base), 1), new ProfileRequest
{
    RequestId = Guid.NewGuid().ToString("N"),
    Fetch = LegacyProbeEntityId
});
await ExpectProfileRejected(ServerProcess.FormatName(nameof(ProcessType.GM), 1), new ProfileRequest
{
    RequestId = Guid.NewGuid().ToString("N"),
    Fetch = neighborIds[ServerProcess.FormatName(nameof(ProcessType.Base), 1)]
});

var missing = new Ping { RequestId = Guid.NewGuid().ToString("N"), EntityId = Guid.NewGuid().ToString("N"), Entry = nameof(ProcessType.Base) };
baseRoute.Input.Writer.TryWrite(new Frame { Ping = missing });
var notFound = (await baseRoute.Output.Reader.ReadAsync(stop)).Pong;
if (notFound?.RequestId != missing.RequestId || notFound.Status != Pong.Types.Status.NotFound || !routes.Ready)
{
    throw new Exception("A missing entity interrupted a healthy stream.");
}

foreach (var stream in neighbors.Values)
{
    stream.Input.Writer.Complete();
}

await routing.WaitAsync(stop);
Console.WriteLine("PASS GUID-only initialization and registration, Profile routing and authorization, handshake retry, WSS/gRPC gating, cancellation, disconnect and missing entity");
return 0;

async Task ExpectProfileRejected(string peer, ProfileRequest request)
{
    var stream = neighbors[peer];
    stream.Input.Writer.TryWrite(new Frame { ProfileRequest = request });
    var reply = (await stream.Output.Reader.ReadAsync(stop)).ProfileReply;
    if (reply?.RequestId != request.RequestId || reply.Result != ProfileReply.Types.Result.Rejected || !routes.Ready)
    {
        throw new Exception("Profile accepted an invalid subject, foreign mutation or unauthorized caller.");
    }

    if (profileRoute.Output.Reader.TryRead(out _))
    {
        throw new Exception("Rejected Profile request reached the Profile service.");
    }
}

Task Open(ServerNode node, string peer, FrameStream stream, string[]? ids = null)
{
    stream.Input.Writer.TryWrite(new Frame { Hello = peer });
    return node.RunLinkAsync(peer, Guid.NewGuid().ToString("N"), ids ?? [], stream, stream, stop);
}

async Task ExpectPong(Pong.Types.Status status)
{
    var ping = new Ping { RequestId = Guid.NewGuid().ToString("N"), EntityId = point.State.EntityId, Entry = nameof(ProcessType.Gate) };
    login.Input.Writer.TryWrite(new Frame { Ping = ping });
    var response = (await login.Output.Reader.ReadAsync(stop)).Pong;
    if (response?.RequestId != ping.RequestId || response.Status != status)
    {
        throw new Exception($"Expected {status} for the Gate entity, got {response}.");
    }
}

sealed class WaitingSinglePoint : ServerSinglePoint
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task InitializeRoleAsync(CancellationToken stop)
    {
        Started.SetResult();
        return Release.Task.WaitAsync(stop);
    }
}

sealed class FrameStream : IAsyncStreamReader<Frame>, IAsyncStreamWriter<Frame>
{
    public Channel<Frame> Input { get; } = Channel.CreateUnbounded<Frame>();
    public Channel<Frame> Output { get; } = Channel.CreateUnbounded<Frame>();
    public Frame Current { get; private set; } = null!;
    public WriteOptions? WriteOptions
    {
        get; set;
    }

    public Task WriteAsync(Frame frame) => Output.Writer.WriteAsync(frame).AsTask();
    public Task BeforeWrite { get; init; } = Task.CompletedTask;
    public async Task WriteAsync(Frame frame, CancellationToken stop)
    {
        await BeforeWrite.WaitAsync(stop);
        await Output.Writer.WriteAsync(frame, stop);
    }

    public async Task<bool> MoveNext(CancellationToken stop)
    {
        if (!await Input.Reader.WaitToReadAsync(stop))
        {
            return false;
        }

        Current = await Input.Reader.ReadAsync(stop);
        return true;
    }
}

sealed class WebSocketUpgrade : IHttpWebSocketFeature
{
    public bool IsWebSocketRequest => true;
    public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
        throw new Exception("WebSocket was accepted during role initialization.");
}
