using System.Net;
using System.Text;
using System.Globalization;
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

    public static Task RegisterProcessAsync(ServerProcess process, string address, CancellationToken stop)
        => Instance.RegisterProcessCoreAsync(process, address, stop);

    private async Task RegisterProcessCoreAsync(ServerProcess process, string address, CancellationToken stop)
    {
        session.CheckActive();
        stop.ThrowIfCancellationRequested();
        var processes = ServerConfiguration.Processes;
        var path = $"{options.DiscoveryRoot}/{NodePrefix}{process.Name}";
        try
        {
            await zk.createAsync(path, Encoding.UTF8.GetBytes(address), ZooDefs.Ids.OPEN_ACL_UNSAFE,
                CreateMode.EPHEMERAL).WaitAsync(stop).ConfigureAwait(false);
        }
        catch (KeeperException.NodeExistsException error)
        {
            throw new InvalidOperationException($"Cannot start {process.Name}: ZooKeeper node '{path}' already exists.", error);
        }
        catch
        {
            // 写入取消时结果可能未知，关闭会话统一清理可能已创建的临时节点。
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }

        session.CheckActive();
        GameLog.Info(ServerLogEvents.ZkRegister, process.Role, detail: address);
        var names = (await zk.getChildrenAsync(options.DiscoveryRoot).WaitAsync(stop).ConfigureAwait(false)).Children;
        var registered = new List<string>();
        foreach (var peer in processes)
        {
            if (names.Contains(NodePrefix + peer.Name))
            {
                registered.Add(peer.Name);
            }
        }

        GameLog.Info(ServerLogEvents.ZkDiscover, process.Role, detail: string.Join(",", registered));
    }

    public static Task<string> ResolveProcessAsync(ServerProcess peer, CancellationToken stop)
        => Instance.ResolveProcessCoreAsync(peer, stop);

    private async Task<string> ResolveProcessCoreAsync(ServerProcess peer, CancellationToken stop)
    {
        var path = $"{options.DiscoveryRoot}/{NodePrefix}{peer.Name}";
        while (true)
        {
            session.CheckActive();
            stop.ThrowIfCancellationRequested();
            try
            {
                var data = await zk.getDataAsync(path).WaitAsync(stop).ConfigureAwait(false);
                session.CheckActive();
                var uri = new Uri(Encoding.UTF8.GetString(data.Data));
                var expectedScheme = Uri.UriSchemeHttp;
                if (peer.Type == ProcessType.Gate)
                {
                    expectedScheme = Uri.UriSchemeHttps;
                }

                if (uri.Host != IPAddress.Loopback.ToString() || uri.Scheme != expectedScheme)
                {
                    throw new InvalidDataException($"Unexpected server endpoint for {peer.Name}.");
                }

                return uri.GetLeftPart(UriPartial.Authority);
            }
            catch (KeeperException.NoNodeException)
            {
                var changed = new Changed();
                if (await zk.existsAsync(path, changed).WaitAsync(stop).ConfigureAwait(false) is null)
                {
                    await changed.Signal.Task.WaitAsync(stop).ConfigureAwait(false);
                }
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
        internal TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task process(WatchedEvent evt)
        {
            Signal.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
