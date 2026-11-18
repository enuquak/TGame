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
const string InvalidEntityId = "body-1";
const int TestWorkerId = 0;

if (args is ["--configuration"])
{
    ServerConfigurationChecks.Run();
    return 0;
}

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

using var logLifetime = GameLog.Initialize();

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

if (args is ["--zookeeper-session"])
{
    await ZooKeeperProxyChecks.CheckSessionAsync();
    await IdGeneratorLifecycleChecks.RunAsync();
    return 0;
}

ServerConfigurationChecks.Run();
SinglePointChecks.CheckBeforeIdInitialization();
await IdGeneratorLifecycleChecks.RunAsync();
// 内存检查共用一个进程内生成器，不向真实服务器组发布 ID。
await using var idLifetime = await IdGenerator.InitializeAsync(
    _ => Task.FromResult(new SnowflakeIdGenerator(TestWorkerId, TimeProvider.System)));

if (args is ["--dynamic-topology"] or ["--discovery-links"] or ["--startup-publication"])
{
    try
    {
        using var dynamicDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (args[0] == "--startup-publication")
        {
            await StartupPublicationChecks.RunAsync(dynamicDeadline.Token);
        }
        else if (args[0] == "--discovery-links")
        {
            await DiscoveryLinkChecks.RunAsync(dynamicDeadline.Token);
        }
        else
        {
            await DynamicTopologyChecks.RunAsync(dynamicDeadline.Token);
        }
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

if (args is ["--singlepoint-dispatch"])
{
    using var dispatchDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));
    await SinglePointDispatchChecks.RunAsync(dispatchDeadline.Token);
    return 0;
}

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
    using var routingDeadline = new CancellationTokenSource(PeerLink.RequestTimeout * 2 + TimeSpan.FromSeconds(TestTimeoutSeconds));
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
    await IdGeneratorChecks.RunZooKeeperAsync(idConfig);
    return 0;
}

if (args is ["--registry"])
{
    try
    {
        await ZooKeeperProxyChecks.RunAsync();
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
if (engineAssembly == typeof(ProfileServerSinglePoint).Assembly)
{
    throw new Exception("Engine primitives and business handlers must be compiled into separate assemblies.");
}

Type[] engineTypes = [typeof(LinkService), typeof(ServerNode), typeof(PeerLink), typeof(ZooKeeperProxy),
    typeof(ServerSinglePoint), typeof(RouterServerSinglePoint), typeof(SinglePointRegistry), typeof(SinglePointForAttribute)];
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

await RpcChecks.RunAsync(stop);
await MessageDispatchChecks.RunAsync(stop);
await CheckInitializationCancellationAsync();
await GroupChecks.RunAsync(stop);

using var configuration = TestNodes.InitializeConfiguration(TestNodes.DefaultGroup);
var defaults = new SinglePointRegistry([]);
using var loginNode = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Login), 1));
var router = new FrameStream();
var routerLink = Open(loginNode, ServerProcess.FormatName(nameof(ProcessType.Router), 1), router);
var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var pendingGate = Open(loginNode, ServerProcess.FormatName(nameof(ProcessType.Gate), 1), new FrameStream { BeforeWrite = handshake.Task });
if (!loginNode.FirstLinks.IsCompletedSuccessfully)
{
    Console.Error.WriteLine("Login waited for an incoming Gate despite its outgoing Router handshake being complete.");
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
var profileCall = loginNode.CallSinglePointAsync<ProfileReply>(ProcessType.Profile, profileRequest, stop);
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

var failedCall = loginNode.CallSinglePointAsync<ProfileReply>(ProcessType.Profile, profileRequest, stop);
var failedFrame = await router.Output.Reader.ReadAsync(stop);
router.Input.Writer.TryWrite(new Frame
{
    Reply = new RpcReply
    {
        RequestId = failedFrame.Request.RequestId,
        Error = new RpcError { StatusCode = (int)StatusCode.Unavailable, Detail = "Profile disconnected." }
    }
});
try
{
    await failedCall;
    throw new Exception("Profile transport failure was not returned to the caller.");
}
catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
{
}

using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop))
{
    cancelled.Cancel();
    try
    {
        await loginNode.CallSinglePointAsync<ProfileReply>(ProcessType.Profile, profileRequest, cancelled.Token);
        throw new Exception("Profile swallowed caller cancellation.");
    }
    catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
    {
    }
}

retry.Input.Writer.Complete();
router.Input.Writer.Complete();
await Task.WhenAll(accepted, routerLink).WaitAsync(stop);
try
{
    await loginNode.CallSinglePointAsync<ProfileReply>(ProcessType.Profile, profileRequest, stop);
    throw new Exception("Disconnected Profile caller did not report Unavailable.");
}
catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
{
}

var business = new SinglePointRegistry([typeof(WaitingSinglePoint)]);
var constructedSinglePoints = WaitingSinglePoint.CreatedCount;
using var gate = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1));
try
{
    await gate.InitializeSinglePointAsync(business, stop);
    throw new Exception("SinglePoint initialization started before the engine was ready.");
}
catch (InvalidOperationException)
{
}

if (WaitingSinglePoint.CreatedCount != constructedSinglePoints || gate.SinglePoint is not null)
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

var initialization = gate.InitializeSinglePointAsync(business, stop);
var point = gate.SinglePoint as WaitingSinglePoint ?? throw new Exception("The registered business SinglePoint was not selected.");
await point.Started.Task.WaitAsync(stop);
if (!IdText.IsValid(point.EntityId) || point.LocalEntityIds.Any())
{
    throw new Exception("A SinglePoint must own its identity without registering before business initialization completes.");
}

point.Release.SetResult();
await initialization.WaitAsync(stop);
if (!gate.Ready)
{
    throw new Exception("Gate remained unavailable after role initialization.");
}

login.Input.Writer.Complete();
basePeer.Input.Writer.Complete();
await streams.WaitAsync(stop);
if (gate.Ready)
{
    throw new Exception("Gate remained available after a peer disconnected.");
}

InitializationFailureSinglePoint failedPoint;
using (var failedNode = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1)))
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

using var routes = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Router), 1));
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
foreach (var pair in ServerConfiguration.DialTargets)
{
    if (!pair.Value.Contains(ProcessType.Router))
    {
        continue;
    }

    var peer = ServerProcess.FormatName(pair.Key.ToString(), 1);
    neighbors.Add(peer, new FrameStream());
    neighborIds.Add(peer, IdGenerator.GenId());
}

var routeTasks = new List<Task>();
var routeRegistrations = new List<Task>();
foreach (var pair in neighbors)
{
    routeTasks.Add(Open(routes, pair.Key, pair.Value));
    var hello = await pair.Value.Output.Reader.ReadAsync(stop);
    if (hello.Hello != routes.Name)
    {
        throw new Exception("Router did not acknowledge the process handshake.");
    }

    routeRegistrations.Add(TestNodes.RegisterAsync(pair.Value, [neighborIds[pair.Key]], stop));
}

var routing = Task.WhenAll(routeTasks);
await routes.FirstLinks.WaitAsync(stop);
await routes.InitializeSinglePointAsync(defaults, stop);
await Task.WhenAll(routeRegistrations);
var baseRoute = neighbors[ServerProcess.FormatName(nameof(ProcessType.Base), 1)];
var profileRoute = neighbors[ServerProcess.FormatName(nameof(ProcessType.Profile), 1)];
var query = new ProfileRequest
{
    RequestId = IdGenerator.GenId(),
    Fetch = neighborIds[ServerProcess.FormatName(nameof(ProcessType.Scene), 1)]
};
baseRoute.Input.Writer.TryWrite(ProfileRpcChecks.Request(ServerProcess.FormatName(nameof(ProcessType.Base), 1), query));
var forwarded = await profileRoute.Output.Reader.ReadAsync(stop);
if (!forwarded.ReadRequest<ProfileRequest>().Equals(query))
{
    throw new Exception("Router did not forward the query to the Profile role.");
}

profileRoute.Input.Writer.TryWrite(RpcFrames.Reply(new ProfileReply
    {
        RequestId = query.RequestId,
        EntityId = query.Fetch,
        Result = ProfileReply.Types.Result.Missing
    }, forwarded.Request.RequestId));
var queried = (await baseRoute.Output.Reader.ReadAsync(stop)).ReadReply<ProfileReply>();
if (queried.RequestId != query.RequestId || queried.EntityId != query.Fetch || queried.Result != ProfileReply.Types.Result.Missing)
{
    throw new Exception("Profile reply did not return to the query caller.");
}

foreach (var stream in neighbors.Values)
{
    stream.Input.Writer.Complete();
}

await routing.WaitAsync(stop);
Console.WriteLine("PASS engine-first initialization, optional business construction, failure disposal, Profile routing, handshake retry, readiness gating, cancellation and disconnect.");
return 0;

async Task CheckInitializationCancellationAsync()
{
    using var isolatedConfiguration = TestNodes.InitializeIsolated(ProcessType.Gate);
    using var cancellingNode = TestNodes.Create(nameof(ProcessType.Gate));
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
    var business = new SinglePointRegistry([typeof(WaitingSinglePoint)]);
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
}

Task Open(ServerNode node, string peer, FrameStream stream)
{
    stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
    return node.RunLinkAsync(peer, IdGenerator.GenId(), stream, stream, stop);
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
