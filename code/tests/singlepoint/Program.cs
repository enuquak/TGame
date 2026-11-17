using Empty = Google.Protobuf.WellKnownTypes.Empty;
using TGame.Transport;
using TGame.Profile;
using System.Threading.Channels;
using Grpc.Core;
using TGame;
using TGame.Server;
using TGame.Server.Engine;
using TGame.Server.Logic;

const int TestTimeoutSeconds = 5;
const int HandshakeObservationMilliseconds = 200;
const string InvalidEntityId = "body-1";
const int TestWorkerId = 0;

if (args is ["--logging"])
{
    try
    {
        LogLevelChecks.Run();
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

if (args is ["--profile-storage", var profileIdConfig])
{
    using var profileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try
    {
        await ProfileStorageChecks.RunAsync(profileIdConfig, profileDeadline.Token);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

SinglePointChecks.CheckBeforeIdInitialization();
await IdGeneratorLifecycleChecks.RunAsync();
// 内存检查共用一个进程内生成器，不向真实服务器组发布 ID。
await using var idLifetime = await IdGenerator.InitializeAsync(
    _ => Task.FromResult(new SnowflakeIdGenerator(TestWorkerId, TimeProvider.System)));

if (args is ["--profile"])
{
    using var profileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));
    try
    {
        await ProfileRpcChecks.RunAsync(profileDeadline.Token);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

if (args is ["--routing"])
{
    using var routingDeadline = new CancellationTokenSource(PeerLink.RequestTimeout + TimeSpan.FromSeconds(TestTimeoutSeconds));
    try
    {
        await EntityRegistrationChecks.RunAsync(routingDeadline.Token);
        await EntityRoutingChecks.RunAsync(routingDeadline.Token);
        await EntityRoutingChecks.CheckLocalDeadlineAsync(routingDeadline.Token);
        await GroupChecks.RunAsync(routingDeadline.Token);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

await IdGeneratorChecks.RunAsync();
if (args is ["--engine"])
{
    await SinglePointChecks.RunAsync();
    using var engineDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));
    RpcChecks.CheckTransportBoundary();
    await RpcChecks.RunAsync(engineDeadline.Token);
    await MessageDispatchChecks.RunAsync(engineDeadline.Token);
    return 0;
}

if (args is ["--id-zookeeper", var idConfig])
{
    await IdGeneratorChecks.RunZooKeeperAsync(idConfig, ServerSettings.Current.Value.ZooKeeper);
    return 0;
}

if (args is ["--registry"])
{
    try
    {
        await ServiceRegistryChecks.RunAsync(ServerSettings.Current.Value.ZooKeeper, ServerSettings.Current.Value.DiscoveryRoot);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

SettingsChecks.Run();
RpcChecks.CheckTransportBoundary();
await SinglePointChecks.RunAsync();

var engineAssembly = typeof(Topology).Assembly;
if (engineAssembly == typeof(ServerMessages).Assembly)
{
    throw new Exception("Engine primitives and business handlers must be compiled into separate assemblies.");
}

Type[] engineTypes = [typeof(LinkService), typeof(ServerNode), typeof(PeerLink), typeof(ServiceRegistry),
    typeof(ServerSinglePoint), typeof(SinglePointRegistry), typeof(SinglePointForAttribute)];
foreach (var type in engineTypes)
{
    if (type.Assembly != engineAssembly)
    {
        throw new Exception($"{type.Name} must belong to the engine assembly.");
    }
}

if (args is not ["--node"])
{
    await MongoStorageChecks.RunAsync();
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));
var stop = deadline.Token;
var group = new ServerGroup(Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1));

await RpcChecks.RunAsync(stop);
await MessageDispatchChecks.RunAsync(stop);

var defaults = new SinglePointRegistry([]);
using var loginNode = TestNodes.Create(ServerProcess.FormatName(nameof(ProcessType.Login), 1), group);
var router = new FrameStream();
var routerLink = Open(loginNode, ServerProcess.FormatName(nameof(ProcessType.Router), 1), router);
var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var pendingGate = Open(loginNode, ServerProcess.FormatName(nameof(ProcessType.Gate), 1), new FrameStream { BeforeWrite = handshake.Task });
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
var accepted = Open(loginNode, ServerProcess.FormatName(nameof(ProcessType.Gate), 1), retry);
await loginNode.FirstLinks.WaitAsync(stop);
await loginNode.InitializeSinglePointAsync(defaults, stop);
if (!loginNode.Ready || loginNode.SinglePoint?.GetType() != typeof(ServerSinglePoint))
{
    throw new Exception("A connected role with no business SinglePoint did not become ready.");
}

var profileRequest = new ProfileRequest { RequestId = IdGenerator.GenId(), Fetch = IdGenerator.GenId() };
var profileCall = loginNode.CallProfileAsync(profileRequest, stop);
var profileFrame = await router.Output.Reader.ReadAsync(stop);
if (profileFrame.Request.SinglePointType != nameof(ProcessType.Profile) ||
    profileFrame.Request.SourceProcess != loginNode.Name ||
    profileFrame.Request.RequestId == profileRequest.RequestId ||
    !profileFrame.ReadRequest<ProfileRequest>().Equals(profileRequest))
{
    throw new Exception("Profile request payload changed during transport.");
}

router.Input.Writer.TryWrite(RpcFrames.Reply(new ProfileReply
    {
        RequestId = profileRequest.RequestId,
        EntityId = profileRequest.Fetch,
        Result = ProfileReply.Types.Result.Missing
    }, profileFrame.Request.RequestId));
if ((await profileCall.WaitAsync(stop)).Result != ProfileReply.Types.Result.Missing)
{
    throw new Exception("Profile reply was not delivered to its caller.");
}

var failedCall = loginNode.CallProfileAsync(profileRequest, stop);
var failedFrame = await router.Output.Reader.ReadAsync(stop);
router.Input.Writer.TryWrite(new Frame
{
    Reply = new RpcReply
    {
        RequestId = failedFrame.Request.RequestId,
        Error = new RpcError { StatusCode = (int)StatusCode.Unavailable, Detail = "Profile disconnected." }
    }
});
if ((await failedCall).Result != ProfileReply.Types.Result.Unavailable)
{
    throw new Exception("Profile transport failure did not preserve Unavailable.");
}

using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop))
{
    cancelled.Cancel();
    try
    {
        await loginNode.CallProfileAsync(profileRequest, cancelled.Token);
        throw new Exception("Profile swallowed caller cancellation.");
    }
    catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
    {
    }
}

retry.Input.Writer.Complete();
router.Input.Writer.Complete();
await Task.WhenAll(accepted, routerLink).WaitAsync(stop);
if ((await loginNode.CallProfileAsync(profileRequest, stop)).Result != ProfileReply.Types.Result.Unavailable)
{
    throw new Exception("Disconnected Profile caller did not preserve Unavailable.");
}

var business = new SinglePointRegistry([typeof(WaitingSinglePoint)]);
using var gate = TestNodes.Create(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), group);
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
var streams = Task.WhenAll(Open(gate, ServerProcess.FormatName(nameof(ProcessType.Login), 1), login), Open(gate, ServerProcess.FormatName(nameof(ProcessType.Base), 1), basePeer));
await gate.FirstLinks.WaitAsync(stop);
if (gate.Ready || gate.SinglePoint is not null)
{
    throw new Exception("Engine readiness must not create or initialize a business SinglePoint.");
}

await ExpectProfileResult(ProfileReply.Types.Result.Unavailable);
var initialization = gate.InitializeSinglePointAsync(business, stop);
var point = gate.SinglePoint as WaitingSinglePoint ?? throw new Exception("The registered business SinglePoint was not selected.");
await point.Started.Task.WaitAsync(stop);
if (!IdText.IsValid(point.EntityId) || point.LocalEntityIds.Any())
{
    throw new Exception("A SinglePoint must own its identity without registering before business initialization completes.");
}

await ExpectProfileResult(ProfileReply.Types.Result.Unavailable);
point.Release.SetResult();
await initialization.WaitAsync(stop);
if (!gate.Ready)
{
    throw new Exception("Gate remained unavailable after role initialization.");
}

await ExpectProfileResult(ProfileReply.Types.Result.Rejected);
login.Input.Writer.Complete();
basePeer.Input.Writer.Complete();
await streams.WaitAsync(stop);
if (gate.Ready)
{
    throw new Exception("Gate remained available after a peer disconnected.");
}

using var cancellingNode = TestNodes.CreateIsolated(ProcessType.Gate);
using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
var pending = cancellingNode.InitializeSinglePointAsync(business, cancel.Token);
var cancelling = (WaitingSinglePoint)cancellingNode.SinglePoint!;
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
if (cancelling.Initialized || cancellingNode.Ready || cancelling.LocalEntityIds.Any())
{
    throw new Exception("Cancelled SinglePoint became ready.");
}

InitializationFailureSinglePoint failedPoint;
using (var failedNode = TestNodes.Create(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), group))
{
    var failedLogin = new FrameStream();
    var failedBase = new FrameStream();
    var failedLinks = Task.WhenAll(Open(failedNode, ServerProcess.FormatName(nameof(ProcessType.Login), 1), failedLogin),
        Open(failedNode, ServerProcess.FormatName(nameof(ProcessType.Base), 1), failedBase));
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

using var routes = TestNodes.Create(ServerProcess.FormatName(nameof(ProcessType.Router), 1), group);
var duplicateEntityId = IdGenerator.GenId();
string[][] invalidRegistrations = [[], [InvalidEntityId], [duplicateEntityId, duplicateEntityId]];
foreach (var ids in invalidRegistrations)
{
    var stream = new FrameStream();
    var running = Open(routes, ServerProcess.FormatName(nameof(ProcessType.Base), 1), stream);
    await stream.Output.Reader.ReadAsync(stop);
    try
    {
        await TestNodes.RegisterAsync(stream, ids, stop);
        throw new Exception("Router accepted an empty, non-GUID or duplicate entity registration.");
    }
    catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
    {
    }
    finally
    {
        stream.Input.Writer.Complete();
        await running.WaitAsync(stop);
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
    neighborIds.Add(peer, IdGenerator.GenId());
}

var routeTasks = new List<Task>();
foreach (var pair in neighbors)
{
    routeTasks.Add(Open(routes, pair.Key, pair.Value));
    var hello = await pair.Value.Output.Reader.ReadAsync(stop);
    if (hello.Hello != routes.Name)
    {
        throw new Exception("Router did not acknowledge the process handshake.");
    }

    await TestNodes.RegisterAsync(pair.Value, [neighborIds[pair.Key]], stop);
}

var routing = Task.WhenAll(routeTasks);
await routes.FirstLinks.WaitAsync(stop);
await routes.InitializeSinglePointAsync(defaults, stop);
var baseRoute = neighbors[ServerProcess.FormatName(nameof(ProcessType.Base), 1)];
var profileRoute = neighbors[ServerProcess.FormatName(nameof(ProcessType.Profile), 1)];
var publish = new ProfileRequest
{
    RequestId = IdGenerator.GenId(),
    Publish = new EntitySummary
    {
        EntityId = neighborIds[ServerProcess.FormatName(nameof(ProcessType.Base), 1)],
        DisplayName = "TGame profile",
        Title = "Profile check"
    }
};
baseRoute.Input.Writer.TryWrite(ProfileRpcChecks.Request(ServerProcess.FormatName(nameof(ProcessType.Base), 1), publish));
var forwarded = await profileRoute.Output.Reader.ReadAsync(stop);
if (forwarded.ReadRequest<ProfileRequest>()?.RequestId != publish.RequestId || forwarded.ReadRequest<ProfileRequest>().Publish?.EntityId != publish.Publish.EntityId)
{
    throw new Exception("Router did not forward an owned GUID entity to the Profile role.");
}

profileRoute.Input.Writer.TryWrite(RpcFrames.Reply(new ProfileReply
    {
        RequestId = publish.RequestId,
        EntityId = publish.Publish.EntityId,
        Result = ProfileReply.Types.Result.Stored
    }, forwarded.Request.RequestId));
var published = (await baseRoute.Output.Reader.ReadAsync(stop)).ReadReply<ProfileReply>();
if (published?.RequestId != publish.RequestId || published.EntityId != publish.Publish.EntityId || published.Result != ProfileReply.Types.Result.Stored)
{
    throw new Exception("Profile reply did not return to the owner of the GUID entity.");
}

var foreignPublish = publish.Clone();
foreignPublish.RequestId = IdGenerator.GenId();
foreignPublish.Publish.EntityId = neighborIds[ServerProcess.FormatName(nameof(ProcessType.Scene), 1)];
await ExpectProfileRejected(ServerProcess.FormatName(nameof(ProcessType.Base), 1), foreignPublish);
await ExpectProfileRejected(ServerProcess.FormatName(nameof(ProcessType.Base), 1), new ProfileRequest
{
    RequestId = IdGenerator.GenId(),
    Withdraw = neighborIds[ServerProcess.FormatName(nameof(ProcessType.Scene), 1)]
});
await ExpectProfileRejected(ServerProcess.FormatName(nameof(ProcessType.Base), 1), new ProfileRequest
{
    RequestId = IdGenerator.GenId(),
    Fetch = InvalidEntityId
});
await ExpectProfileRejected(ServerProcess.FormatName(nameof(ProcessType.GM), 1), new ProfileRequest
{
    RequestId = IdGenerator.GenId(),
    Fetch = neighborIds[ServerProcess.FormatName(nameof(ProcessType.Base), 1)]
});

var missing = RpcFrames.Request(new Empty(), IdGenerator.GenId());
missing.Request.SourceProcess = ServerProcess.FormatName(nameof(ProcessType.Base), 1);
missing.Request.EntityId = IdGenerator.GenId();
missing.Request.DeadlineUnixMs = DateTimeOffset.UtcNow.Add(PeerLink.RequestTimeout).ToUnixTimeMilliseconds();
baseRoute.Input.Writer.TryWrite(missing);
var notFound = (await baseRoute.Output.Reader.ReadAsync(stop)).Reply;
if (notFound.RequestId != missing.Request.RequestId || notFound.Error?.StatusCode != (int)StatusCode.NotFound || !routes.Ready)
{
    throw new Exception("A missing entity interrupted a healthy stream.");
}

foreach (var stream in neighbors.Values)
{
    stream.Input.Writer.Complete();
}

await routing.WaitAsync(stop);
await GroupChecks.RunAsync(stop);
Console.WriteLine("PASS engine-first initialization, optional business construction, failure disposal, Profile routing, handshake retry, readiness gating, cancellation and disconnect.");
return 0;

async Task ExpectProfileRejected(string peer, ProfileRequest request)
{
    var stream = neighbors[peer];
    stream.Input.Writer.TryWrite(ProfileRpcChecks.Request(peer, request));
    var reply = (await stream.Output.Reader.ReadAsync(stop)).ReadReply<ProfileReply>();
    if (reply?.RequestId != request.RequestId || reply.Result != ProfileReply.Types.Result.Rejected || !routes.Ready)
    {
        throw new Exception("Profile accepted an invalid subject, foreign mutation or unauthorized caller.");
    }

    if (profileRoute.Output.Reader.TryRead(out _))
    {
        throw new Exception("Rejected Profile request reached the Profile service.");
    }
}

Task Open(ServerNode node, string peer, FrameStream stream)
{
    stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
    return node.RunLinkAsync(peer, IdGenerator.GenId(), stream, stream, stop);
}

async Task ExpectProfileResult(ProfileReply.Types.Result result)
{
    var request = new ProfileRequest
    {
        RequestId = IdGenerator.GenId(),
        Fetch = IdGenerator.GenId()
    };
    var messages = new RpcDispatcher();
    ServerMessages.Register(messages, gate);
    var response = (ProfileReply)await messages.DispatchEntityAsync(RpcFrames.Request(request).Request, stop);
    if (response.RequestId != request.RequestId || response.Result != result)
    {
        throw new Exception($"Expected {result} for the Profile request, got {response}.");
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

    public bool AcknowledgeRegistration { get; init; } = true;
    public Task WriteAsync(Frame frame) => WriteAsync(frame, CancellationToken.None);
    public Task BeforeWrite { get; init; } = Task.CompletedTask;
    public async Task WriteAsync(Frame frame, CancellationToken stop)
    {
        await BeforeWrite.WaitAsync(stop);
        if (AcknowledgeRegistration && frame.Request?.Payload?.Is(RegisterEntitiesRequest.Descriptor) == true)
        {
            await Input.Writer.WriteAsync(RpcFrames.Reply(new RegisterEntitiesReply(), frame.Request.RequestId), stop);
            return;
        }

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
