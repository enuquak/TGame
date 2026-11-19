using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using TGame.Transport;

namespace TGame.Server.Engine;

public partial class ServerNode
{
    private const int ReconnectDelayMilliseconds = 500;
    private const int RequestQueueCapacity = 64;

    private readonly ServerProcess process;
    private volatile bool linksReady;
    private readonly object linkChanges = new();
    private readonly HashSet<string> sessions = [];
    private readonly TaskCompletionSource firstLinks = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource readinessChanges = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, PeerLink> links = new();

    private readonly GameLog log;
    private readonly IdGenerator ids;
    private readonly ServerGroup startup;
    private readonly ZooKeeperProxy zooKeeper;
    private readonly ServerConfiguration configuration;

    public ServerNode(ServerProcess process, ServerConfiguration configuration, ServerGroup startup, ZooKeeperProxy zooKeeper,
        IdGenerator ids, GameLog log)
    {
        this.log = log;
        this.ids = ids;
        this.zooKeeper = zooKeeper;
        this.configuration = configuration;
        this.startup = startup;
        // 构造时仍校验实例编号，名称只通过身份对象派生。
        _ = process.Name;
        this.process = process;
        UpdateLinkReadiness();
    }

    public string Name => process.Name;

    public Task FirstLinks => firstLinks.Task;
    public string Role => process.Role;
    public bool Ready => linksReady;

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

                changed = readinessChanges.Task;
            }

            await changed.WaitAsync(stop);
        }
    }

    public Task<TReply> CallPeerAsync<TReply>(string peer, IMessage request, CancellationToken stop)
        where TReply : IMessage<TReply>, new()
    {
        ArgumentNullException.ThrowIfNull(request);
        stop.ThrowIfCancellationRequested();
        if (!ServerProcess.TryParse(peer, out _))
        {
            throw new ArgumentException("A process instance name is required.", nameof(peer));
        }

        if (!links.TryGetValue(peer, out var link))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"No connection to {peer}."));
        }

        return link.CallAsync<TReply>(request, ids.GenId(), stop);
    }

    private Frame CreateHello() => new() { Hello = Name };

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

    internal bool AcceptsIncoming(string peer)
    {
        return ServerProcess.TryParse(peer, out var peerProcess) && configuration.Dials(peerProcess.Type, process.Type);
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
        if (!ServerProcess.TryParse(peer, out var peerProcess) || !configuration.Dials(process.Type, peerProcess.Type))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unexpected peer instance {peer}."));
        }

        using var channel = GrpcChannel.ForAddress(address);
        var id = ids.GenId();
        var headers = new Metadata { { TransportProtocol.PeerHeader, Name }, { TransportProtocol.LinkHeader, id } };

        using var call = new ServerLink.ServerLinkClient(channel).Connect(headers, cancellationToken: stop);
        await call.RequestStream.WriteAsync(CreateHello(), stop);
        await RunLinkAsync(peer, id, call.ResponseStream, call.RequestStream, stop);
    }

    public async Task AcceptConnectAsync(string peer, string id, IAsyncStreamReader<Frame> input,
        IAsyncStreamWriter<Frame> output, CancellationToken stop)
    {
        if (!IdText.IsValid(id) || !AcceptsIncoming(peer))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Unexpected link."));
        }

        if (!await zooKeeper.IsProcessClaimedAsync(peer, stop))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Peer identity is not claimed."));
        }

        if (!Ready)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "The receiving process is not ready."));
        }

        await RunLinkAsync(peer, id, input, output, stop, requireReady: true);
    }

    internal async Task RunLinkAsync(string peer, string id, IAsyncStreamReader<Frame> input,
        IAsyncStreamWriter<Frame> output, CancellationToken stop, bool requireReady = false)
    {
        var link = new PeerLink(Role, peer, id, output, log);
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
            if (requireReady)
            {
                await output.WriteAsync(CreateHello(), stop);
            }

            lock (linkChanges)
            {
                links[peer] = link;
                log.Info(ServerLogEvents.LinkUp, Role, peer, link: id);
                UpdateLinkReadiness();
            }

            await RunTransportAsync(link, input, stop);
        }
        finally
        {
            lock (linkChanges)
            {
                sessions.Remove(peer);
                if (links.TryRemove(peer, out _))
                {
                    UpdateLinkReadiness();
                }
            }

            link.Close();
        }
    }

    private static async Task RunTransportAsync(PeerLink link, IAsyncStreamReader<Frame> input, CancellationToken stop)
    {
        // 请求输出与接收分开，写入回包时仍可接收当前连接上的响应。
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
                var request = frame.Request;
                PeerLink.ValidateEnvelope(request.RequestId, request.Payload);
                var reply = new RpcReply
                {
                    RequestId = request.RequestId,
                    Error = new RpcError
                    {
                        StatusCode = (int)StatusCode.Unimplemented,
                        Detail = "This process does not handle RPC requests."
                    }
                };
                await link.WriteAsync(new Frame { Reply = reply }, transport.Token);
            }
        }
    }

    // 调用方持有 linkChanges；首次构造尚未发布节点时除外。
    private bool HasRequiredLinks()
    {
        bool starting = !firstLinks.Task.IsCompletedSuccessfully;
        foreach (var target in configuration.GetDialTargets(process.Type))
        {
            int minimum = startup.GetStartupMinimum(target);
            if (!starting)
            {
                // 启动数量只检查一次；运行中仅跟踪必需类型是否仍有可用连接。
                minimum = Math.Min(minimum, 1);
            }

            int connected = links.Values.Count(link => ServerProcess.Parse(link.Peer).Type == target);
            if (connected < minimum)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateLinkReadiness()
    {
        bool connected = HasRequiredLinks();
        if (connected)
        {
            firstLinks.TrySetResult();
        }

        if (connected == linksReady)
        {
            return;
        }

        linksReady = connected;
        var previous = readinessChanges;
        readinessChanges = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
        if (connected)
        {
            log.Info(ServerLogEvents.LinksReady, Role, detail: links.Count.ToString());
            return;
        }

        log.Error(ServerLogEvents.LinksLost, Role, detail: links.Count.ToString());
    }
}
