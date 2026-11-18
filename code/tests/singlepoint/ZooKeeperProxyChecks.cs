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
        var settings = ServerSettings.Current.Value;
        var path = Path.Combine(AppContext.BaseDirectory, "config", ZooKeeperOptions.FileName);
        return ZooKeeperOptions.Load(path, settings.ZooKeeper, settings.DiscoveryRoot);
    }

    internal static async Task CheckSessionAsync()
    {
        Expect<InvalidOperationException>(() => _ = ZooKeeperProxy.SessionLost);
        var options = new ZooKeeperOptions
        {
            Endpoint = "127.0.0.1:2181",
            DiscoveryRoot = "/checks",
            SessionTimeoutMilliseconds = TimeoutSeconds * 1000,
            ConnectTimeoutMilliseconds = TimeoutSeconds * 1000
        };

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => ZooKeeperProxy.InitializeAsync(options, cancelled.Token));
        await ExpectAsync<ArgumentException>(() => ZooKeeperProxy.InitializeAsync(options with { DiscoveryRoot = "/bad//root" }));
        await ExpectAsync<ArgumentOutOfRangeException>(() => ZooKeeperProxy.InitializeAsync(options with { SessionTimeoutMilliseconds = 0 }));
        Expect<InvalidOperationException>(() => _ = ZooKeeperProxy.SessionLost);

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

    public static async Task RunAsync()
    {
        var options = LoadOptions();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        var stop = deadline.Token;
        var path = options.DiscoveryRoot + "/server-" + Process.Name;
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Router] = 1;
        var group = new ServerGroup(counts);
        using var configuration = ServerConfiguration.Initialize(group);
        await using var proxy = await ZooKeeperProxy.InitializeAsync(options, stop);
        var sessionLost = ZooKeeperProxy.SessionLost;
        Check(!sessionLost.IsCompleted, "Initialization must publish an active shared session.");
        await ExpectAsync<InvalidOperationException>(() => ZooKeeperProxy.InitializeAsync(options, stop));
        var owner = new ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, new WatcherStub());
        try
        {
            // 不覆盖其他会话拥有的节点，也包括本检查的另一个实例。
            await owner.createAsync(path, Encoding.UTF8.GetBytes(Address), ZooDefs.Ids.OPEN_ACL_UNSAFE,
                CreateMode.EPHEMERAL).WaitAsync(stop);

            bool rejected = false;
            try
            {
                await ZooKeeperProxy.RegisterProcessAsync(Process, ReplacementAddress, stop);
            }
            catch (InvalidOperationException error) when (error.InnerException is KeeperException.NodeExistsException)
            {
                rejected = error.Message.Contains(path, StringComparison.Ordinal);
            }

            if (!rejected)
            {
                throw new Exception("An existing fixed service path must fail startup immediately with the conflicting path.");
            }

            var data = await owner.getDataAsync(path).WaitAsync(stop);
            if (Encoding.UTF8.GetString(data.Data) != Address)
            {
                throw new Exception("A failed registration changed the existing service address.");
            }
        }
        finally
        {
            await owner.closeAsync();
        }

        var discovery = ZooKeeperProxy.ResolveProcessAsync(Process, stop);
        await ZooKeeperProxy.RegisterProcessAsync(Process, ReplacementAddress, stop);
        if (await discovery != ReplacementAddress)
        {
            throw new Exception("Registration and discovery must use the same fixed service path.");
        }

        await proxy.DisposeAsync();
        Check(sessionLost.IsCompletedSuccessfully, "Proxy disposal must invalidate all users of the session.");
        Expect<InvalidOperationException>(() => _ = ZooKeeperProxy.SessionLost);
        await using var next = await ZooKeeperProxy.InitializeAsync(options, stop);
        await ZooKeeperProxy.RegisterProcessAsync(Process, Address, stop);
        Check(await ZooKeeperProxy.ResolveProcessAsync(Process, stop) == Address, "The next lifecycle must use its new session.");

        Console.WriteLine("PASS singleton lifecycle, fixed service paths, duplicate rejection, node ownership and discovery across registration.");
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
