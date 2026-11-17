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
    private readonly ConcurrentDictionary<string, byte> localEntities = new();
    private readonly ConcurrentDictionary<string, string> owners = new();
    private readonly ConcurrentDictionary<string, PeerLink> links = new();

    public ServerNode(string name, ServerGroup group, RpcDispatcher messages, IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);
        IdGenerator = idGenerator;
        State = new SinglePointState(idGenerator);
        Name = name;
        this.group = group;
        this.messages = messages;
        Role = group.GetProcess(name).Role;
        localEntities[State.EntityId] = 0;
        if (Role == nameof(ProcessType.Router))
        {
            singlePointIds[ProcessType.Router] = [State.EntityId];
        }

        expectedPeers = Topology.Peers(group, name);
        UpdateLinkReadiness();
    }

    public string Name { get; }

    public Task FirstLinks => firstLinks.Task;
    public string Role { get; }
    public bool Ready => singlePoint?.Initialized == true && linksReady;
    public SinglePointState State { get; }
    public IIdGenerator IdGenerator { get; }
    public ServerSinglePoint? SinglePoint => singlePoint;

    public ProcessType GetProcessType(string process) => group.GetProcess(process).Type;

    public bool IsEntityOwner(string process, string entityId)
    {
        if (process == Name)
        {
            return localEntities.ContainsKey(entityId);
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
        await singlePoint.InitializeAsync(Role, State, IdGenerator, stop);
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

    private string PeerRole(string peer)
    {
        if (peer == EnginePeerNames.Client)
        {
            return peer;
        }

        return group.GetProcess(peer).Role;
    }

    public async Task MaintainLinkAsync(string peer, ServiceRegistry registry, bool reconnect, CancellationToken stop)
    {
        while (true)
        {
            var address = await registry.ResolveAsync(peer, stop);

            try
            {
                await ConnectAsync(peer, address, stop);
                throw new IOException($"Link to {peer} closed.");
            }
            catch (Exception error) when (reconnect && !stop.IsCancellationRequested &&
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
        if (PeerRole(peer) == nameof(ProcessType.Router))
        {
            headers.Add(TransportProtocol.EntityHeader, string.Join(',', localEntities.Keys));
        }

        using var call = new ServerLink.ServerLinkClient(channel).Connect(headers, cancellationToken: stop);
        await call.RequestStream.WriteAsync(CreateHello(), stop);
        await RunLinkAsync(peer, id, [], call.ResponseStream, call.RequestStream, stop);
    }

    public async Task RunLinkAsync(string peer, string id, string[] entities, IAsyncStreamReader<Frame> input,
        IAsyncStreamWriter<Frame> output, CancellationToken stop)
    {
        if (!expectedPeers.Contains(peer))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unexpected peer instance {peer}."));
        }

        var link = new PeerLink(Role, peer, id, output);
        if (Role == nameof(ProcessType.Router))
        {
            bool hasInvalidIds = entities.Any(entity => !IdText.IsValid(entity));
            bool hasDuplicateIds = entities.Distinct().Count() != entities.Length;
            if (entities.Length == 0 || hasInvalidIds || hasDuplicateIds)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid entity registration from {peer}."));
            }
        }

        if (!await input.MoveNext(stop) || input.Current.Hello != peer)
        {
            throw new IOException($"Link to {peer} closed before handshake.");
        }

        var peerPoint = input.Current.SinglePointId;
        ValidateSinglePoint(peer, peerPoint, entities);

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
                if (Role == nameof(ProcessType.Router) && entities.Any(entity => owners.ContainsKey(entity) || localEntities.ContainsKey(entity)))
                {
                    throw new InvalidDataException($"Duplicate entity from {peer}.");
                }

                links[peer] = link;
                RegisterPeerSinglePoint(peer, peerPoint);
                if (Role == nameof(ProcessType.Router))
                {
                    foreach (var entity in entities)
                    {
                        owners[entity] = peer;
                        TraceLog.Event(ServerLogEvents.EntityRoute, Role, peer, entity: entity, link: id);
                    }
                }

                TraceLog.Event(ServerLogEvents.LinkUp, Role, peer, link: id);
                UpdateLinkReadiness();
            }

            // Handlers can issue nested requests on the same stream.
            // Keep receiving replies while a handler awaits its result.
            using var transport = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var queue = Channel.CreateBounded<Frame>(new BoundedChannelOptions(RequestQueueCapacity) { SingleReader = true, SingleWriter = true });
            var read = ReadAsync();
            var serve = ServeAsync();
            var finished = await Task.WhenAny(read, serve);

            transport.Cancel();
            queue.Writer.TryComplete();
            link.Close();
            try
            {
                await Task.WhenAll(read, serve);
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

            async Task ServeAsync()
            {
                var running = new List<Task>();
                try
                {
                    await foreach (var frame in queue.Reader.ReadAllAsync(transport.Token))
                    {
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
                    foreach (var entity in entities)
                    {
                        owners.TryRemove(entity, out _);
                    }

                    UpdateLinkReadiness();
                }
            }

            link.Close();
        }
    }

    private void UpdateLinkReadiness()
    {
        bool complete = expectedPeers.All(links.ContainsKey);
        if (complete == linksReady)
        {
            return;
        }

        linksReady = complete;
        TraceLog.Event(complete ? ServerLogEvents.LinksReady : ServerLogEvents.LinksLost, Role, detail: links.Count.ToString());
        if (complete)
        {
            firstLinks.TrySetResult();
        }
    }
}
