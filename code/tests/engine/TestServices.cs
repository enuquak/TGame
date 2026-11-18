using TGame;
using TGame.Server.Engine;

// 显式传递测试依赖，不使用进程全局状态；本地检查不连接 ZooKeeper。
internal sealed class TestServices : IAsyncDisposable
{
    public GameLog Log { get; } = new();
    public IdGenerator Ids { get; } = new(_ => Task.FromResult(new SnowflakeIdGenerator(0, TimeProvider.System)));
    public ZooKeeperProxy ZooKeeper { get; }

    public TestServices()
    {
        ZooKeeper = new ZooKeeperProxy(LocalZooKeeperOptions(), Log);
    }

    public static ZooKeeperOptions LocalZooKeeperOptions() => new()
    {
        Endpoint = "127.0.0.1:1",
        DiscoveryRoot = "/local-checks",
        SessionTimeoutMilliseconds = 1000,
        ConnectTimeoutMilliseconds = 1000
    };

    public ServerNode CreateNode(ServerProcess process, ServerConfiguration configuration) =>
        new(process, configuration, ZooKeeper, Ids, Log);

    public async ValueTask DisposeAsync()
    {
        await Ids.DisposeAsync();
        await ZooKeeper.DisposeAsync();
        Log.Dispose();
    }
}
