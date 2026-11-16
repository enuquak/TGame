using TGame.Diagnostics;
using TGame.Transport;
using TGame.Profile;
using System.Net.WebSockets;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TGame;
using TGame.Server.Engine;
using TGame.Server.Logic;

const int TestTimeoutSeconds = 5;
const int HandshakeObservationMilliseconds = 200;
const string LegacyProbeEntityId = "body-1";

if (args is ["--registry"])
{
    try
    {
        await ServiceRegistryChecks.RunAsync(TestStorage.Settings.Value.ZooKeeper);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

await SinglePointChecks.RunAsync();

var engineAssembly = typeof(Topology).Assembly;
if (engineAssembly == typeof(GameServerNode).Assembly)
{
    throw new Exception("Engine primitives and the business node must be compiled into separate assemblies.");
}

Type[] engineTypes = [typeof(LinkService), typeof(ServerNode), typeof(PeerLink), typeof(ServiceRegistry),
    typeof(ServerSinglePoint), typeof(SinglePointState), typeof(SinglePointRegistry), typeof(SinglePointForAttribute)];
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

var defaults = new SinglePointRegistry([]);
using var loginNode = new GameServerNode(nameof(ProcessType.Login));
var router = new FrameStream();
var routerLink = Open(loginNode, nameof(ProcessType.Router), router);
var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var pendingGate = Open(loginNode, nameof(ProcessType.Gate), new FrameStream { BeforeWrite = handshake.Task });
if (await Task.WhenAny(loginNode.FirstLinks, Task.Delay(HandshakeObservationMilliseconds, stop)) == loginNode.FirstLinks)
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
var accepted = Open(loginNode, nameof(ProcessType.Gate), retry);
await loginNode.FirstLinks.WaitAsync(stop);
await loginNode.InitializeSinglePointAsync(defaults, stop);
if (!loginNode.Ready || loginNode.SinglePoint?.GetType() != typeof(ServerSinglePoint))
{
    throw new Exception("A connected role with no business SinglePoint did not become ready.");
}

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

var business = new SinglePointRegistry([typeof(WaitingSinglePoint)]);
using var gate = new GameServerNode(nameof(ProcessType.Gate));
try
{
    await gate.InitializeSinglePointAsync(business, stop);
    throw new Exception("SinglePoint initialization started before the engine was ready.");
}
catch (InvalidOperationException)
{
}

if (WaitingSinglePoint.CreatedCount != 0 || gate.SinglePoint is not null)
{
    throw new Exception("A business SinglePoint was constructed before the handshakes.");
}

var login = new FrameStream();
var basePeer = new FrameStream();
var streams = Task.WhenAll(Open(gate, nameof(ProcessType.Login), login), Open(gate, nameof(ProcessType.Base), basePeer));
await gate.FirstLinks.WaitAsync(stop);
if (gate.Ready || gate.SinglePoint is not null)
{
    throw new Exception("Engine readiness must not create or initialize a business SinglePoint.");
}

await ExpectPong(Pong.Types.Status.Unavailable);
var initialization = gate.InitializeSinglePointAsync(business, stop);
var point = gate.SinglePoint as WaitingSinglePoint ?? throw new Exception("The registered business SinglePoint was not selected.");
await point.Started.Task.WaitAsync(stop);
if (!ReferenceEquals(point.State, gate.State))
{
    throw new Exception("SinglePoint initialization replaced the identity already registered by the engine.");
}

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
var pending = cancelling.InitializeAsync(nameof(ProcessType.Gate), new SinglePointState(), cancel.Token);
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

InitializationFailureSinglePoint failedPoint;
using (var failedNode = new GameServerNode(nameof(ProcessType.Gate)))
{
    var failedLogin = new FrameStream();
    var failedBase = new FrameStream();
    var failedLinks = Task.WhenAll(Open(failedNode, nameof(ProcessType.Login), failedLogin),
        Open(failedNode, nameof(ProcessType.Base), failedBase));
    await failedNode.FirstLinks.WaitAsync(stop);

    var failingRegistry = new SinglePointRegistry([typeof(InitializationFailureSinglePoint)]);
    try
    {
        await failedNode.InitializeSinglePointAsync(failingRegistry, stop);
        throw new Exception("Business initialization failure was hidden.");
    }
    catch (InvalidDataException)
    {
    }

    failedPoint = failedNode.SinglePoint as InitializationFailureSinglePoint
        ?? throw new Exception("A failed business SinglePoint was replaced with a default instance.");
    if (failedNode.Ready || failedPoint.Initialized)
    {
        throw new Exception("A failed business SinglePoint became ready.");
    }

    failedLogin.Input.Writer.Complete();
    failedBase.Input.Writer.Complete();
    await failedLinks.WaitAsync(stop);
}

if (!failedPoint.Disposed)
{
    throw new Exception("Failed SinglePoint resources were not disposed with the engine node.");
}

using var routes = new GameServerNode(nameof(ProcessType.Router));
var duplicateEntityId = Guid.NewGuid().ToString("N");
string[][] invalidRegistrations = [[], [LegacyProbeEntityId], [duplicateEntityId, duplicateEntityId]];
foreach (var ids in invalidRegistrations)
{
    try
    {
        await Open(routes, nameof(ProcessType.Base), new FrameStream(), ids).WaitAsync(stop);
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

    neighbors.Add(pair.Key, new FrameStream());
    neighborIds.Add(pair.Key, Guid.NewGuid().ToString("N"));
}

var routeTasks = new List<Task>();
foreach (var pair in neighbors)
{
    routeTasks.Add(Open(routes, pair.Key, pair.Value, [neighborIds[pair.Key]]));
}

var routing = Task.WhenAll(routeTasks);
await routes.FirstLinks.WaitAsync(stop);
await routes.InitializeSinglePointAsync(defaults, stop);
foreach (var stream in neighbors.Values)
{
    var reply = await stream.Output.Reader.ReadAsync(stop);
    if (reply.Hello != nameof(ProcessType.Router))
    {
        throw new Exception("Router did not acknowledge a GUID-only registration.");
    }
}

var baseRoute = neighbors[nameof(ProcessType.Base)];
var profileRoute = neighbors[nameof(ProcessType.Profile)];
var publish = new ProfileRequest
{
    RequestId = Guid.NewGuid().ToString("N"),
    Publish = new EntitySummary
    {
        EntityId = neighborIds[nameof(ProcessType.Base)],
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
foreignPublish.Publish.EntityId = neighborIds[nameof(ProcessType.Scene)];
await ExpectProfileRejected(nameof(ProcessType.Base), foreignPublish);
await ExpectProfileRejected(nameof(ProcessType.Base), new ProfileRequest
{
    RequestId = Guid.NewGuid().ToString("N"),
    Withdraw = neighborIds[nameof(ProcessType.Scene)]
});
await ExpectProfileRejected(nameof(ProcessType.Base), new ProfileRequest
{
    RequestId = Guid.NewGuid().ToString("N"),
    Fetch = LegacyProbeEntityId
});
await ExpectProfileRejected(nameof(ProcessType.GM), new ProfileRequest
{
    RequestId = Guid.NewGuid().ToString("N"),
    Fetch = neighborIds[nameof(ProcessType.Base)]
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
Console.WriteLine("PASS engine-first initialization, optional business construction, failure disposal, Profile routing, handshake retry, WSS/gRPC gating, cancellation and disconnect.");
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
    var ping = new Ping { RequestId = Guid.NewGuid().ToString("N"), EntityId = gate.State.EntityId, Entry = nameof(ProcessType.Gate) };
    login.Input.Writer.TryWrite(new Frame { Ping = ping });
    var response = (await login.Output.Reader.ReadAsync(stop)).Pong;
    if (response?.RequestId != ping.RequestId || response.Status != status)
    {
        throw new Exception($"Expected {status} for the Gate entity, got {response}.");
    }
}

[SinglePointFor(ProcessType.Gate)]
sealed class WaitingSinglePoint : ServerSinglePoint
{
    public static int CreatedCount { get; private set; }

    public WaitingSinglePoint()
    {
        CreatedCount++;
    }

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
