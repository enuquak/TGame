using System.Net;
using System.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using org.apache.zookeeper;

namespace TGame.Server.Engine;

public sealed class ZooKeeperProxy : IAsyncDisposable
{
    private const string NodePrefix = "server-";
    private const int WorkerReuseWaitMilliseconds = 1;
    private ZooKeeper? zk;
    private readonly GameLog log;
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly ZooKeeperOptions options;
    private readonly ZooKeeperSession session = new();
    private bool disposed;
    private volatile bool initialized;
    private ServerProcess? claimedProcess;
    private int claiming;

    public ZooKeeperProxy(ZooKeeperOptions options, GameLog log)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        this.log = log;
    }

    private ZooKeeper Client
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!initialized)
            {
                throw new InvalidOperationException("Initialize the process ZooKeeper proxy before use.");
            }

            return zk!;
        }
    }

    public Task SessionLost
    {
        get
        {
            _ = Client;
            return session.Lost.Task;
        }
    }

    public ServerProcess Process
    {
        get
        {
            session.CheckActive();
            return claimedProcess ?? throw new InvalidOperationException("Claim the process identity before use.");
        }
    }

    // 容器只负责创建对象，连接建立由宿主显式等待。
    public async Task InitializeAsync(CancellationToken stop = default)
    {
        await lifecycle.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (zk is not null)
            {
                throw new InvalidOperationException("The process ZooKeeper proxy is already initialized.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(options.ConnectTimeoutMilliseconds);
            try
            {
                zk = new ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, session);
                await session.Connected.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                session.CheckActive();
                initialized = true;
            }
            catch
            {
                await CloseAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task<ServerProcess> ClaimProcessAsync(ProcessType type, CancellationToken stop)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        // 尚未建立会话时不进入占号流程，避免误关闭正在初始化的代理。
        _ = Client;
        session.CheckActive();
        if (Interlocked.Exchange(ref claiming, 1) != 0)
        {
            throw new InvalidOperationException("This process has already requested an identity.");
        }

        try
        {
            await EnsurePathAsync(options.DiscoveryRoot, stop).ConfigureAwait(false);
            for (int number = 1; ; number = checked(number + 1))
            {
                session.CheckActive();
                stop.ThrowIfCancellationRequested();
                var process = new ServerProcess(type, number);
                // 空地址只占用编号，进程初始化完成后再发布给发现者。
                if (!await TryCreateEphemeralAsync(ProcessPath(process), stop).ConfigureAwait(false))
                {
                    continue;
                }

                session.CheckActive();
                claimedProcess = process;
                return process;
            }
        }
        catch
        {
            // 创建结果不确定时关闭会话，不换号留下多个身份。
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RegisterProcessAsync(ServerProcess process, string address, CancellationToken stop)
    {
        session.CheckActive();
        if (claimedProcess != process)
        {
            throw new InvalidOperationException("Only the identity claimed by this process can be published.");
        }

        ValidateAddress(process, address);
        try
        {
            await Client.setDataAsync(ProcessPath(process), Encoding.UTF8.GetBytes(address))
                .WaitAsync(stop).ConfigureAwait(false);
            session.CheckActive();
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }

        log.Info(ServerLogEvents.ZkRegister, process.Role, detail: address);
    }

    public async IAsyncEnumerable<IReadOnlyList<DiscoveredProcess>> WatchProcessesAsync(
        [EnumeratorCancellation] CancellationToken stop)
    {
        await EnsurePathAsync(options.DiscoveryRoot, stop).ConfigureAwait(false);
        var changed = new Changed();
        while (true)
        {
            session.CheckActive();
            stop.ThrowIfCancellationRequested();
            var children = await Client.getChildrenAsync(options.DiscoveryRoot, changed).WaitAsync(stop).ConfigureAwait(false);
            var processes = new List<DiscoveredProcess>();
            foreach (var child in children.Children)
            {
                var process = await ReadDiscoveredProcessAsync(child, changed, stop).ConfigureAwait(false);
                if (process is not null)
                {
                    processes.Add(process);
                }
            }

            session.CheckActive();
            yield return processes;
            await changed.WaitAsync(stop).ConfigureAwait(false);
        }
    }

    private async Task<DiscoveredProcess?> ReadDiscoveredProcessAsync(string child, Watcher changed, CancellationToken stop)
    {
        if (!child.StartsWith(NodePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (!ServerProcess.TryParse(child[NodePrefix.Length..], out var process))
        {
            log.Error(ServerLogEvents.ZkDiscover, claimedProcess?.Role ?? nameof(ZooKeeperProxy),
                detail: $"忽略非法进程节点 {child}");
            return null;
        }

        try
        {
            // 同时监听地址发布/变化；父节点 Watch 只覆盖成员增删。
            var data = await Client.getDataAsync(ProcessPath(process), changed).WaitAsync(stop).ConfigureAwait(false);
            if (data.Data.Length == 0)
            {
                return null;
            }

            var address = ValidateAddress(process, Encoding.UTF8.GetString(data.Data));
            if (data.Stat.getEphemeralOwner() == 0)
            {
                throw new InvalidDataException($"Process {process.Name} must use an ephemeral node.");
            }

            return new DiscoveredProcess(process, address, data.Stat.getCzxid());
        }
        catch (KeeperException.NoNodeException)
        {
            // 快照读取期间成员离开，父节点 Watch 会触发下一次收敛。
            return null;
        }
        catch (InvalidDataException error)
        {
            // 单条无效记录不能使已有健康连接停止；数据 Watch 保留以等待修正。
            log.Error(ServerLogEvents.ZkDiscover, claimedProcess?.Role ?? nameof(ZooKeeperProxy),
                peer: process.Name, detail: error.Message);
            return null;
        }
    }

    public async Task<bool> IsProcessClaimedAsync(string name, CancellationToken stop)
    {
        session.CheckActive();
        if (!ServerProcess.TryParse(name, out var process))
        {
            return false;
        }

        var stat = await Client.existsAsync(ProcessPath(process)).WaitAsync(stop).ConfigureAwait(false);
        session.CheckActive();
        return stat is not null && stat.getEphemeralOwner() != 0;
    }

    private string ProcessPath(ServerProcess process) => $"{options.DiscoveryRoot}/{NodePrefix}{process.Name}";

    private static string ValidateAddress(ServerProcess process, string address)
    {
        var scheme = process.Type == ProcessType.Gate ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Host != IPAddress.Loopback.ToString() ||
            uri.Scheme != scheme ||
            uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
        {
            throw new InvalidDataException($"Unexpected server endpoint for {process.Name}.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private async Task EnsurePathAsync(string root, CancellationToken stop)
    {
        var path = "";
        foreach (var segment in root[1..].Split('/'))
        {
            session.CheckActive();
            path += "/" + segment;
            try
            {
                await Client.createAsync(path, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT)
                    .WaitAsync(stop).ConfigureAwait(false);
            }
            catch (KeeperException.NodeExistsException)
            {
            }
        }
    }

    private async Task<bool> TryCreateEphemeralAsync(string path, CancellationToken stop)
    {
        try
        {
            await Client.createAsync(path, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL)
                .WaitAsync(stop).ConfigureAwait(false);
            return true;
        }
        catch (KeeperException.NodeExistsException)
        {
            return false;
        }
    }

    internal async Task<WorkerLease> AcquireWorkerAsync(string root, CancellationToken stop)
    {
        ZooKeeperOptions.ValidatePath(root);
        _ = Client;
        session.CheckActive();
        stop.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(options.ConnectTimeoutMilliseconds);
        var token = timeout.Token;
        try
        {
            await EnsurePathAsync(root, token).ConfigureAwait(false);
            for (int workerId = 0; workerId <= SnowflakeSequence.MaxWorkerId; workerId++)
            {
                session.CheckActive();
                token.ThrowIfCancellationRequested();
                var workerPath = root + "/" + workerId.ToString(CultureInfo.InvariantCulture);
                if (!await TryCreateEphemeralAsync(workerPath, token).ConfigureAwait(false))
                {
                    continue;
                }

                // 编号复用必须进入申请后的下一毫秒，等待同样受申请超时约束。
                long allocatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= allocatedAt)
                {
                    await Task.Delay(WorkerReuseWaitMilliseconds, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                session.CheckActive();
                return new WorkerLease(this, workerId, workerPath);
            }

            throw new InvalidOperationException("All Snowflake worker IDs are occupied.");
        }
        catch
        {
            // 申请未完成就终止共享会话，避免遗留无法确认归属的 worker 节点。
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask ReleaseWorkerAsync(string path)
    {
        if (disposed || session.Lost.Task.IsCompleted)
        {
            return;
        }

        try
        {
            await Client.deleteAsync(path).WaitAsync(TimeSpan.FromMilliseconds(options.ConnectTimeoutMilliseconds)).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task CloseAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        session.Invalidate();
        if (zk is not null)
        {
            await zk.closeAsync().ConfigureAwait(false);
        }
    }

    internal sealed class WorkerLease(ZooKeeperProxy proxy, int workerId, string path) : IAsyncDisposable
    {
        private ZooKeeperProxy? owner = proxy;
        internal int WorkerId { get; } = workerId;
        internal ZooKeeperSession Session { get; } = proxy.session;

        public async ValueTask DisposeAsync()
        {
            var previous = Interlocked.Exchange(ref owner, null);
            if (previous is not null)
            {
                await previous.ReleaseWorkerAsync(path).ConfigureAwait(false);
            }
        }
    }

    private sealed class Changed : Watcher
    {
        private readonly Channel<byte> changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });

        internal ValueTask<byte> WaitAsync(CancellationToken stop) => changes.Reader.ReadAsync(stop);

        public override Task process(WatchedEvent evt)
        {
            changes.Writer.TryWrite(0);
            return Task.CompletedTask;
        }
    }
}
