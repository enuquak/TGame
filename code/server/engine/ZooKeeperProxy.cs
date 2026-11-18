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
    private static ZooKeeperProxy? instance;
    private readonly ZooKeeper zk;
    private readonly ZooKeeperOptions options;
    private readonly ZooKeeperSession session = new();
    private bool disposed;
    private ServerProcess? claimedProcess;
    private int claiming;

    private ZooKeeperProxy(ZooKeeperOptions options)
    {
        this.options = options;
        zk = new ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, session);
    }

    private static ZooKeeperProxy Instance => Volatile.Read(ref instance)
        ?? throw new InvalidOperationException("Initialize the process ZooKeeper proxy before use.");

    public static Task SessionLost => Instance.session.Lost.Task;

    // 宿主顺序初始化和释放，其他模块通过静态入口共用同一会话。
    public static async Task<ZooKeeperProxy> InitializeAsync(ZooKeeperOptions options, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        stop.ThrowIfCancellationRequested();
        if (instance is not null)
        {
            throw new InvalidOperationException("The process ZooKeeper proxy is already initialized.");
        }

        var proxy = new ZooKeeperProxy(options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(options.ConnectTimeoutMilliseconds);
        try
        {
            await proxy.session.Connected.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            proxy.session.CheckActive();
            Volatile.Write(ref instance, proxy);
            return proxy;
        }
        catch
        {
            await proxy.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static Task<ServerProcess> ClaimProcessAsync(ProcessType type, CancellationToken stop)
        => Instance.ClaimProcessCoreAsync(type, stop);

    private async Task<ServerProcess> ClaimProcessCoreAsync(ProcessType type, CancellationToken stop)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

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
                var path = ProcessPath(process);
                try
                {
                    // 空地址只占用编号，引擎和业务初始化完成后再发布给发现者。
                    await zk.createAsync(path, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL)
                        .WaitAsync(stop).ConfigureAwait(false);
                }
                catch (KeeperException.NodeExistsException)
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

    public static Task RegisterProcessAsync(ServerProcess process, string address, CancellationToken stop)
        => Instance.RegisterProcessCoreAsync(process, address, stop);

    private async Task RegisterProcessCoreAsync(ServerProcess process, string address, CancellationToken stop)
    {
        session.CheckActive();
        if (claimedProcess != process)
        {
            throw new InvalidOperationException("Only the identity claimed by this process can be published.");
        }

        ValidateAddress(process, address);
        try
        {
            await zk.setDataAsync(ProcessPath(process), Encoding.UTF8.GetBytes(address))
                .WaitAsync(stop).ConfigureAwait(false);
            session.CheckActive();
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }

        GameLog.Info(ServerLogEvents.ZkRegister, process.Role, detail: address);
    }

    public static IAsyncEnumerable<IReadOnlyList<DiscoveredProcess>> WatchProcessesAsync(CancellationToken stop)
        => Instance.WatchProcessesCoreAsync(stop);

    private async IAsyncEnumerable<IReadOnlyList<DiscoveredProcess>> WatchProcessesCoreAsync(
        [EnumeratorCancellation] CancellationToken stop)
    {
        await EnsurePathAsync(options.DiscoveryRoot, stop).ConfigureAwait(false);
        var changed = new Changed();
        while (true)
        {
            session.CheckActive();
            stop.ThrowIfCancellationRequested();
            var children = await zk.getChildrenAsync(options.DiscoveryRoot, changed).WaitAsync(stop).ConfigureAwait(false);
            var processes = new List<DiscoveredProcess>();
            foreach (var child in children.Children)
            {
                if (!child.StartsWith(NodePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!ServerProcess.TryParse(child[NodePrefix.Length..], out var process))
                {
                    GameLog.Error(ServerLogEvents.ZkDiscover, claimedProcess?.Role ?? nameof(ZooKeeperProxy),
                        detail: $"忽略非法进程节点 {child}");
                    continue;
                }
                try
                {
                    // 同时监听地址发布/变化；父节点 Watch 只覆盖成员增删。
                    var data = await zk.getDataAsync(ProcessPath(process), changed).WaitAsync(stop).ConfigureAwait(false);
                    if (data.Data.Length == 0)
                    {
                        continue;
                    }

                    var address = ValidateAddress(process, Encoding.UTF8.GetString(data.Data));
                    if (data.Stat.getEphemeralOwner() == 0)
                    {
                        throw new InvalidDataException($"Process {process.Name} must use an ephemeral node.");
                    }

                    processes.Add(new DiscoveredProcess(process, address, data.Stat.getCzxid()));
                }
                catch (KeeperException.NoNodeException)
                {
                    // 快照读取期间成员离开，父节点 Watch 会触发下一次收敛。
                }
                catch (InvalidDataException error)
                {
                    // 单条无效记录不能使已有健康连接停止；数据 Watch 保留以等待修正。
                    GameLog.Error(ServerLogEvents.ZkDiscover, claimedProcess?.Role ?? nameof(ZooKeeperProxy),
                        peer: process.Name, detail: error.Message);
                }
            }

            session.CheckActive();
            yield return processes;
            await changed.WaitAsync(stop).ConfigureAwait(false);
        }
    }

    public static Task<bool> IsProcessClaimedAsync(string name, CancellationToken stop)
        => Instance.IsProcessClaimedCoreAsync(name, stop);

    private async Task<bool> IsProcessClaimedCoreAsync(string name, CancellationToken stop)
    {
        session.CheckActive();
        if (!ServerProcess.TryParse(name, out var process))
        {
            return false;
        }

        var stat = await zk.existsAsync(ProcessPath(process)).WaitAsync(stop).ConfigureAwait(false);
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
                await zk.createAsync(path, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT)
                    .WaitAsync(stop).ConfigureAwait(false);
            }
            catch (KeeperException.NodeExistsException)
            {
            }
        }
    }

    internal static Task<WorkerLease> AcquireWorkerAsync(string root, CancellationToken stop)
        => Instance.AcquireWorkerCoreAsync(root, stop);

    private async Task<WorkerLease> AcquireWorkerCoreAsync(string root, CancellationToken stop)
    {
        ZooKeeperOptions.ValidatePath(root);
        session.CheckActive();
        stop.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(options.ConnectTimeoutMilliseconds);
        var token = timeout.Token;
        try
        {
            var path = "";
            foreach (var segment in root[1..].Split('/'))
            {
                path += "/" + segment;
                try
                {
                    await zk.createAsync(path, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT)
                        .WaitAsync(token).ConfigureAwait(false);
                }
                catch (KeeperException.NodeExistsException)
                {
                }
            }

            for (int workerId = 0; workerId <= SnowflakeSequence.MaxWorkerId; workerId++)
            {
                session.CheckActive();
                token.ThrowIfCancellationRequested();
                var workerPath = root + "/" + workerId.ToString(CultureInfo.InvariantCulture);
                try
                {
                    await zk.createAsync(workerPath, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL)
                        .WaitAsync(token).ConfigureAwait(false);
                }
                catch (KeeperException.NodeExistsException)
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
            await zk.deleteAsync(path).WaitAsync(TimeSpan.FromMilliseconds(options.ConnectTimeoutMilliseconds)).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        session.Invalidate();
        if (ReferenceEquals(Volatile.Read(ref instance), this))
        {
            Volatile.Write(ref instance, null);
        }

        await zk.closeAsync().ConfigureAwait(false);
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
