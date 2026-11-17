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

    private readonly ServerGroup group;
    private readonly RpcDispatcher messages;
    private readonly HashSet<string> expectedPeers;
    private volatile bool linksReady;
    private volatile ServerSinglePoint? singlePoint;
    private readonly object linkChanges = new();
    private readonly HashSet<string> sessions = [];
    private readonly TaskCompletionSource firstLinks = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, string> owners = new();
    private readonly ConcurrentDictionary<string, PeerLink> links = new();

    public ServerNode(string name, ServerGroup group, RpcDispatcher messages)
    {
        Name = name;
        this.group = group;
        this.messages = messages;
        Role = group.GetProcess(name).Role;
        expectedPeers = Topology.Peers(group, name);
        UpdateLinkReadiness();
    }

    public string Name { get; }

    public Task FirstLinks => firstLinks.Task;
    public string Role { get; }
    public bool Ready => singlePoint?.Initialized == true && linksReady && RegistrationsComplete;
    public ServerSinglePoint? SinglePoint => singlePoint;

    public ProcessType GetProcessType(string process) => group.GetProcess(process).Type;

    public bool IsEntityOwner(string process, string entityId)
    {
        if (process == Name)
        {
            return singlePoint?.ContainsEntity(entityId) == true;
        }

        return owners.TryGetValue(entityId, out var owner) && owner == process;
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
        await singlePoint.InitializeAsync(this, stop);
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
        if (!expectedPeers.Contains(peer))
        {
            return false;
        }

        return Topology.Dials[group.GetProcess(peer).Role].Contains(Role);
    }

    private string PeerRole(string peer) => group.GetProcess(peer).Role;

    public async Task MaintainLinkAsync(string peer, ServiceRegistry registry, CancellationToken stop)
    {
        while (true)
        {
            var address = await registry.ResolveAsync(peer, stop);

            try
            {
                await ConnectAsync(peer, address, stop);
                throw new IOException($"Link to {peer} closed.");
            }
            catch (Exception error) when (!stop.IsCancellationRequested &&
                (error is IOException or OperationCanceledException || error is RpcException
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
        IAsyncStreamWriter<Frame> output, CancellationToken stop)
    {
        if (!expectedPeers.Contains(peer))
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
                // 已初始化的 SinglePoint 在新连接上重新登记，握手本身不注册实体。
                var point = singlePoint;
                if (Role != nameof(ProcessType.Gate) && point is not null && point.ContainsEntity(point.EntityId))
                {
                    await EnsureRegistrationAsync(link, transport.Token);
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

                        if (frame.Request.TargetCase == RpcRequest.TargetOneofCase.None && frame.Request.SourceProcess.Length == 0)
                        {
                            await messages.DispatchAsync(frame.Request, peer, link, transport.Token);
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
                    RemovePeerSinglePoint(peer);
                    if (peerEntities.Remove(peer, out var entities))
                    {
                        foreach (var entity in entities)
                        {
                            owners.TryRemove(entity, out _);
                        }
                    }

                    registrations.Remove(link);
                    registeredLinks.Remove(link);
                    UpdateLinkReadiness();
                }
            }

            link.Close();
        }
    }

    private void UpdateLinkReadiness()
    {
        bool connected = expectedPeers.All(links.ContainsKey);
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
