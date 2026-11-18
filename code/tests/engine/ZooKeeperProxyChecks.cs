using System.Text;
using org.apache.zookeeper;
using TGame;
using TGame.Server;
using TGame.Server.Engine;

internal static class ZooKeeperProxyChecks
{
    private static readonly ServerProcess Process = new(ProcessType.Router, 1);
    private const string Address = "http://127.0.0.1:12345";
    private const string ReplacementAddress = "http://127.0.0.1:12346";
    private const int TimeoutSeconds = 10;

    private sealed class WatcherStub : Watcher
    {
        public override Task process(WatchedEvent evt) => Task.CompletedTask;
    }

    internal static ZooKeeperOptions LoadOptions()
    {
        var settings = ServerSettings.LoadSelected();
        var path = Path.Combine(AppContext.BaseDirectory, "config", ZooKeeperOptions.FileName);
        return ZooKeeperOptions.Load(path, settings.ZooKeeper, settings.DiscoveryRoot);
    }

    internal static async Task CheckSessionAsync(GameLog log)
    {
        var options = TestServices.LocalZooKeeperOptions() with { Endpoint = "127.0.0.1:invalid-port" };
        await using var proxy = new ZooKeeperProxy(options, log);
        Expect<InvalidOperationException>(() => _ = proxy.Process);
        Expect<InvalidOperationException>(() => _ = proxy.SessionLost);
        await ExpectAsync<InvalidOperationException>(() => proxy.IsProcessClaimedAsync("Router1", CancellationToken.None));
        await using var earlyClaim = new ZooKeeperProxy(options, log);
        await ExpectAsync<InvalidOperationException>(() => earlyClaim.ClaimProcessAsync(ProcessType.Router, CancellationToken.None));
        // 过早认领应只拒绝调用；之后初始化仍应进入地址解析，而非实例已被释放。
        await ExpectAsync<FormatException>(() => earlyClaim.InitializeAsync());

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var writeZkLog = ZooKeeper.LogToFile;
        ZooKeeper.LogToFile = false;
        using var pendingStop = new CancellationTokenSource();
        await using var pending = new ZooKeeperProxy(options with { Endpoint = listener.LocalEndpoint.ToString()! }, log);
        var initializing = pending.InitializeAsync(pendingStop.Token);
        try
        {
            // 本地端口只接收连接，不完成 ZK 握手；初始化期间不能公开会话。
            Expect<InvalidOperationException>(() => _ = pending.SessionLost);
        }
        finally
        {
            pendingStop.Cancel();
            try
            {
                await ExpectAsync<OperationCanceledException>(() => initializing);
            }
            finally
            {
                listener.Stop();
                ZooKeeper.LogToFile = writeZkLog;
            }
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => proxy.InitializeAsync(cancelled.Token));
        Expect<ArgumentException>(() => new ZooKeeperProxy(options with { DiscoveryRoot = "/bad//root" }, log));
        Expect<ArgumentOutOfRangeException>(() => new ZooKeeperProxy(options with { SessionTimeoutMilliseconds = 0 }, log));
        Expect<InvalidOperationException>(() => _ = proxy.Process);

        var initial = new ZooKeeperSession();
        initial.StateChanged(Watcher.Event.KeeperState.Disconnected);
        Expect<InvalidOperationException>(initial.CheckActive);
        Check(!initial.Lost.Task.IsCompleted, "An initial connection attempt must not lose an unestablished session.");
        initial.StateChanged(Watcher.Event.KeeperState.SyncConnected);
        initial.CheckActive();
        initial.Invalidate();
        Expect<InvalidOperationException>(initial.CheckActive);

        foreach (var state in new[] { Watcher.Event.KeeperState.Disconnected, Watcher.Event.KeeperState.Expired, Watcher.Event.KeeperState.AuthFailed })
        {
            var session = new ZooKeeperSession();
            session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
            session.CheckActive();
            session.StateChanged(state);
            Check(session.Lost.Task.IsCompletedSuccessfully, "A failed session must notify all users.");
            Expect<InvalidOperationException>(session.CheckActive);
            session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
            Expect<InvalidOperationException>(session.CheckActive);
        }

        var rejected = new ZooKeeperSession();
        rejected.StateChanged(Watcher.Event.KeeperState.AuthFailed);
        Check(rejected.Connected.Task.IsCompletedSuccessfully, "Authentication failure must unblock initialization.");
        Expect<InvalidOperationException>(rejected.CheckActive);
        Console.WriteLine("PASS ZooKeeper initialization guards and terminal session loss after disconnect, expiry, auth failure and disposal.");
    }

    public static async Task RunAsync(GameLog log)
    {
        var configured = LoadOptions();
        var options = configured with { DiscoveryRoot = configured.DiscoveryRoot + "/dynamic-check-" + Guid.NewGuid().ToString("N") };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        var stop = deadline.Token;
        var owner = new ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, new WatcherStub());
        await using var proxy = new ZooKeeperProxy(options, log);
        await proxy.InitializeAsync(stop);
        var path = options.DiscoveryRoot + "/server-Router1";
        var scenePath = options.DiscoveryRoot + "/server-Scene1";
        try
        {
            var prefix = "";
            foreach (var segment in options.DiscoveryRoot[1..].Split('/'))
            {
                prefix += "/" + segment;
                try
                {
                    await owner.createAsync(prefix, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT).WaitAsync(stop);
                }
                catch (KeeperException.NodeExistsException)
                {
                }
            }

            await owner.createAsync(path, Encoding.UTF8.GetBytes(Address), ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(stop);
            var process = await proxy.ClaimProcessAsync(ProcessType.Router, stop);
            Check(process.Name == "Router2", "An occupied number must be skipped without overwriting its owner.");
            var original = await owner.getDataAsync(path).WaitAsync(stop);
            Check(Encoding.UTF8.GetString(original.Data) == Address, "Claim changed another process's address.");
            await ExpectAsync<InvalidOperationException>(() => proxy.ClaimProcessAsync(ProcessType.Router, stop));
            await ExpectAsync<InvalidOperationException>(() => proxy.RegisterProcessAsync(Process, Address, stop));

            await using (var discovery = proxy.WatchProcessesAsync(stop).GetAsyncEnumerator(stop))
            {
                Check(await discovery.MoveNextAsync(), "Discovery did not produce its initial snapshot.");
                Check(discovery.Current.Count == 1, "A reserved identity without an endpoint must not be dialed.");
                await proxy.RegisterProcessAsync(process, ReplacementAddress, stop);
                await Until(items => items.Any(peer => peer.Process.Name == "Router2"));
                await owner.createAsync(options.DiscoveryRoot + "/server-Unknown1", [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(stop);
                await Until(items => items.Count == 2);
                await owner.createAsync(scenePath, Encoding.UTF8.GetBytes(Address), ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(stop);
                await Until(items => items.Any(peer => peer.Process.Name == "Scene1"));
                long generation = discovery.Current.Single(peer => peer.Process.Name == "Scene1").Generation;
                await owner.deleteAsync(scenePath).WaitAsync(stop);
                await owner.createAsync(scenePath, Encoding.UTF8.GetBytes(Address), ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(stop);
                await Until(items => items.Any(peer => peer.Process.Name == "Scene1" && peer.Generation != generation));
                await owner.setDataAsync(scenePath, Encoding.UTF8.GetBytes(ReplacementAddress)).WaitAsync(stop);
                await Until(items => items.Any(peer => peer.Process.Name == "Scene1" && peer.Address == ReplacementAddress));
                await owner.deleteAsync(scenePath).WaitAsync(stop);
                await Until(items => items.All(peer => peer.Process.Name != "Scene1"));

                async Task Until(Func<IReadOnlyList<DiscoveredProcess>, bool> predicate)
                {
                    do
                    {
                        Check(await discovery.MoveNextAsync(), "Discovery ended before a member change.");
                    }
                    while (!predicate(discovery.Current));
                }
            }

            var lost = proxy.SessionLost;
            await proxy.DisposeAsync();
            Check(lost.IsCompletedSuccessfully, "Disposal must invalidate the session.");
            await owner.deleteAsync(path).WaitAsync(stop);
            await using var next = new ZooKeeperProxy(options, log);
            await next.InitializeAsync(stop);
            var reclaimed = await next.ClaimProcessAsync(ProcessType.Router, stop);
            Check(reclaimed.Name == "Router1", "A released number should be reusable.");
            await next.RegisterProcessAsync(reclaimed, Address, stop);
            Check(await next.IsProcessClaimedAsync(reclaimed.Name, stop), "Published identity is not discoverable.");
            Console.WriteLine("PASS ZK number contention, ownership, publication watches, dynamic joins/leaves, same-name replacement and reuse.");
        }
        finally
        {
            await proxy.DisposeAsync();
            // 只清理本检查生成的独立根目录。
            var children = await owner.getChildrenAsync(options.DiscoveryRoot);
            foreach (var child in children.Children)
            {
                await owner.deleteAsync(options.DiscoveryRoot + "/" + child);
            }

            await owner.deleteAsync(options.DiscoveryRoot);
            await owner.closeAsync();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
