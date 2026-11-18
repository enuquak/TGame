using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using TGame.Transport;

namespace TGame.Server.Engine;

public partial class ServerNode : IDisposable
{
    private const int ReconnectDelayMilliseconds = 500;
    private const int RequestQueueCapacity = 64;

    private readonly ProcessType type;
    private readonly IReadOnlyList<ProcessType> requiredTypes;
    private volatile bool linksReady;
    private volatile ServerSinglePoint? singlePoint;
    private readonly object linkChanges = new();
    private readonly HashSet<string> sessions = [];
    private readonly TaskCompletionSource firstLinks = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ServerSinglePoint> singlePointCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, PeerLink> links = new();

    public ServerNode(string name)
    {
        Name = name;
        var process = ServerProcess.Parse(name);
        type = process.Type;
        Role = process.Role;
        requiredTypes = Topology.DialTargets(type);
        UpdateLinkReadiness();
    }

    public string Name { get; }

    public Task FirstLinks => firstLinks.Task;
    public string Role { get; }
    public bool Ready => singlePoint?.Initialized == true && linksReady && RegistrationsComplete;
    public ServerSinglePoint? SinglePoint => singlePoint;

    public async Task WaitUntilReadyAsync(CancellationToken stop)
    {
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            Task changed;
            lock (linkChanges)
            {
                if (Ready)
                {
                    return;
                }

                changed = registrationChanges.Task;
            }

            await changed.WaitAsync(stop);
        }
    }

    public async Task InitializeSinglePointAsync(SinglePointRegistry registry, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        if (!firstLinks.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("Engine connections must be initialized before the SinglePoint.");
        }

        if (singlePoint is not null)
        {
            throw new InvalidOperationException("SinglePoint initialization has already started.");
        }

        singlePoint = registry.Create(Enum.Parse<ProcessType>(Role));
        singlePointCreated.SetResult(singlePoint);
        GameLog.Info(ServerLogEvents.InitStepCompleted, Role, entity: singlePoint.EntityId,
            detail: $"{singlePoint.GetType().Name} 实例创建完成");

        await singlePoint.InitializeAsync(this, stop);
        lock (linkChanges)
        {
            UpdateLinkReadiness();
        }
    }

    public void Dispose()
    {
        (singlePoint as IDisposable)?.Dispose();
    }

    private static void ReceiveFrame(Frame frame, PeerLink link, ChannelWriter<Frame> requests)
    {
        switch (frame.BodyCase)
        {
            case Frame.BodyOneofCase.Reply:
                link.Receive(frame.Reply);
                return;

            case Frame.BodyOneofCase.Request:
                if (!requests.TryWrite(frame))
                {
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "Peer request queue is full."));
                }

                return;

            default:
                throw new InvalidDataException("Invalid stream frame.");
        }
    }

    public bool AcceptsIncoming(string peer)
    {
        return ServerProcess.TryParse(peer, out var process) && Topology.Dials(process.Type, type);
    }

    private async Task MaintainLinkAsync(DiscoveredProcess peer, CancellationToken stop)
    {
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            try
            {
                await ConnectAsync(peer.Process.Name, peer.Address, stop);
                throw new IOException($"Link to {peer.Process.Name} closed.");
            }
            catch (Exception error) when (!stop.IsCancellationRequested &&
                (error is IOException or OperationCanceledException or TimeoutException || error is RpcException
                {
                    StatusCode: StatusCode.Unavailable or StatusCode.Cancelled
                    or StatusCode.Internal or StatusCode.AlreadyExists
                }))
            {
                await Task.Delay(ReconnectDelayMilliseconds, stop);
            }
        }
    }

    private async Task ConnectAsync(string peer, string address, CancellationToken stop)
    {
        using var channel = GrpcChannel.ForAddress(address);
        var id = IdGenerator.GenId();
        var headers = new Metadata { { TransportProtocol.PeerHeader, Name }, { TransportProtocol.LinkHeader, id } };

        using var call = new ServerLink.ServerLinkClient(channel).Connect(headers, cancellationToken: stop);
        await call.RequestStream.WriteAsync(CreateHello(), stop);
        await RunLinkAsync(peer, id, call.ResponseStream, call.RequestStream, stop);
    }

    public async Task RunLinkAsync(string peer, string id, IAsyncStreamReader<Frame> input,
        IAsyncStreamWriter<Frame> output, CancellationToken stop, bool requireReady = false)
    {
        if (!ServerProcess.TryParse(peer, out var peerProcess) || !Topology.ArePeers(type, peerProcess.Type))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unexpected peer instance {peer}."));
        }

        var link = new PeerLink(Role, peer, id, output);
        if (!await input.MoveNext(stop) || input.Current.Hello != peer)
        {
            throw new IOException($"Link to {peer} closed before handshake.");
        }

        lock (linkChanges)
        {
            if (requireReady && !Ready)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "The receiving process is not ready."));
            }

            if (!sessions.Add(peer))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"Duplicate link to {peer}."));
            }
        }

        try
        {
            if (AcceptsIncoming(peer))
            {
                await output.WriteAsync(CreateHello(), stop);
            }

            lock (linkChanges)
            {
                links[peer] = link;
                GameLog.Info(ServerLogEvents.LinkUp, Role, peer, link: id);
                UpdateLinkReadiness();
            }

            // Handlers can issue nested requests on the same stream.
            // Keep receiving replies while a handler awaits its result.
            using var transport = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var queue = Channel.CreateBounded<Frame>(new BoundedChannelOptions(RequestQueueCapacity) { SingleReader = true, SingleWriter = true });
            var read = ReadAsync();
            var serve = ServeAsync();
            var registration = RestoreRegistrationAsync();
            var finished = await Task.WhenAny(read, serve, registration);

            transport.Cancel();
            queue.Writer.TryComplete();
            link.Close();
            try
            {
                await Task.WhenAll(read, serve, registration);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                await finished;
            }

            async Task ReadAsync()
            {
                await foreach (var frame in input.ReadAllAsync(transport.Token))
                {
                    ReceiveFrame(frame, link, queue.Writer);
                }
            }

            async Task RestoreRegistrationAsync()
            {
                // 每条连接等待本地实体就绪，覆盖连接早于和晚于初始化的两种情况。
                if (type != ProcessType.Gate)
                {
                    await entitiesReady.Task.WaitAsync(transport.Token);
                    await RegisterLinkAsync(link, transport.Token);
                }

                await Task.Delay(Timeout.Infinite, transport.Token);
            }

            async Task ServeAsync()
            {
                var running = new List<Task>();
                try
                {
                    await foreach (var frame in queue.Reader.ReadAllAsync(transport.Token))
                    {
                        if (frame.Request.Payload?.Is(RegisterEntitiesRequest.Descriptor) == true)
                        {
                            await HandleRegistrationAsync(frame.Request, link, transport.Token);
                            continue;
                        }

                        for (int index = running.Count - 1; index >= 0; index--)
                        {
                            if (running[index].IsCompleted)
                            {
                                await running[index];
                                running.RemoveAt(index);
                            }
                        }

                        if (running.Count == RequestQueueCapacity)
                        {
                            var reply = ErrorReply(frame.Request.RequestId, StatusCode.ResourceExhausted, "Too many active entity RPCs.");
                            await link.WriteAsync(new Frame { Reply = reply }, transport.Token);
                            continue;
                        }

                        // Forwarding may call back through this stream before the original request completes.
                        running.Add(ServeRoutedAsync(frame.Request));
                    }
                }
                finally
                {
                    await Task.WhenAll(running);
                }

                async Task ServeRoutedAsync(RpcRequest request)
                {
                    try
                    {
                        var reply = await RouteReplyAsync(request, peer, transport.Token);
                        await link.WriteAsync(new Frame { Reply = reply }, transport.Token);
                    }
                    catch
                    {
                        transport.Cancel();
                        throw;
                    }
                }
            }
        }
        finally
        {
            lock (linkChanges)
            {
                sessions.Remove(peer);
                if (links.TryRemove(peer, out _))
                {
                    if (peerSinglePoints.Remove(peer, out var singlePointId) && singlePoint is RouterServerSinglePoint router)
                    {
                        router.RemoveEntities(peer, singlePointId);
                    }

                    registeredLinks.Remove(link);
                    UpdateLinkReadiness();
                }
            }

            link.Close();
        }
    }

    // 调用方持有 linkChanges；首次构造尚未发布节点时除外。
    private bool HasRequiredLinks(bool requireRegistration)
    {
        return requiredTypes.All(target => links.Values.Any(link =>
            ServerProcess.Parse(link.Peer).Type == target &&
            (!requireRegistration || registeredLinks.Contains(link))));
    }

    private void UpdateLinkReadiness()
    {
        var previous = registrationChanges;
        registrationChanges = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
        bool connected = HasRequiredLinks(requireRegistration: false);
        if (connected)
        {
            firstLinks.TrySetResult();
        }

        bool complete = connected && (singlePoint?.Initialized != true || RegistrationsComplete);
        if (complete == linksReady)
        {
            return;
        }

        linksReady = complete;
        if (complete)
        {
            GameLog.Info(ServerLogEvents.LinksReady, Role, detail: links.Count.ToString());
            return;
        }

        GameLog.Error(ServerLogEvents.LinksLost, Role, detail: links.Count.ToString());
    }
}
