using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using TGame.Transport;

namespace TGame.Server.Engine;

public abstract class ServerNode : IDisposable
{
    private const int ReconnectDelayMilliseconds = 500;
    private const int RequestQueueCapacity = 64;

    private volatile bool linksReady;
    private volatile ServerSinglePoint? singlePoint;
    private readonly object linkChanges = new();
    private readonly HashSet<string> sessions = [];
    private readonly TaskCompletionSource firstLinks = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected readonly ConcurrentDictionary<string, byte> localEntities = new();
    protected readonly ConcurrentDictionary<string, string> owners = new();
    protected readonly ConcurrentDictionary<string, PeerLink> links = new();

    protected ServerNode(string role)
    {
        Role = role;
        localEntities[State.EntityId] = 0;
    }

    public Task FirstLinks => firstLinks.Task;
    public string Role { get; }
    public bool Ready => singlePoint?.Initialized == true && linksReady;
    public SinglePointState State { get; } = new();
    public ServerSinglePoint? SinglePoint => singlePoint;

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
        await singlePoint.InitializeAsync(Role, State, stop);
    }

    public void Dispose()
    {
        (singlePoint as IDisposable)?.Dispose();
    }

    protected abstract void ReceiveFrame(Frame frame, PeerLink link, ChannelWriter<Frame> requests);

    protected abstract Task ServeFrameAsync(Frame frame, string peer, string id, PeerLink link, CancellationToken stop);

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
        var id = Guid.NewGuid().ToString("N");
        var headers = new Metadata { { TransportProtocol.PeerHeader, Role }, { TransportProtocol.LinkHeader, id } };
        if (peer == nameof(ProcessType.Router))
        {
            headers.Add(TransportProtocol.EntityHeader, string.Join(',', localEntities.Keys));
        }

        using var call = new ServerLink.ServerLinkClient(channel).Connect(headers, cancellationToken: stop);
        await call.RequestStream.WriteAsync(new Frame { Hello = Role }, stop);
        await RunLinkAsync(peer, id, [], call.ResponseStream, call.RequestStream, stop);
    }

    public async Task RunLinkAsync(string peer, string id, string[] entities, IAsyncStreamReader<Frame> input,
        IAsyncStreamWriter<Frame> output, CancellationToken stop)
    {
        var link = new PeerLink(Role, peer, id, output);
        if (Role == nameof(ProcessType.Router))
        {
            bool hasInvalidIds = entities.Any(entity => !Guid.TryParseExact(entity, "N", out _));
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

        lock (linkChanges)
        {
            if (!sessions.Add(peer))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"Duplicate link to {peer}."));
            }
        }

        try
        {
            if (Topology.Dials[peer].Contains(Role))
            {
                await output.WriteAsync(new Frame { Hello = Role }, stop);
            }

            lock (linkChanges)
            {
                if (Role == nameof(ProcessType.Router) && entities.Any(owners.ContainsKey))
                {
                    throw new InvalidDataException($"Duplicate entity from {peer}.");
                }

                links[peer] = link;
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
                await foreach (var frame in queue.Reader.ReadAllAsync(transport.Token))
                {
                    await ServeFrameAsync(frame, peer, id, link, transport.Token);
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
        bool complete = links.Count == Topology.Degree(Role);
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

    protected static bool IsDeliveryFailure(Exception error) =>
        error is IOException or RpcException or TimeoutException or InvalidOperationException or OperationCanceledException;
}
