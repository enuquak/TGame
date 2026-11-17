namespace TGame.Server.Engine;

public sealed class IdGenerator : IAsyncDisposable
{
    private static IdGenerator? instance;
    private readonly SnowflakeIdGenerator generator;

    private IdGenerator(SnowflakeIdGenerator generator)
    {
        this.generator = generator;
    }

    private static SnowflakeIdGenerator Current => Volatile.Read(ref instance)?.generator
        ?? throw new InvalidOperationException("Initialize the process ID generator before use.");

    public static string GenId() => Current.GenId();

    public static Task LeaseLost => Current.LeaseLost;

    // 宿主按顺序初始化和释放，返回对象只用于 await using 管理生命周期。
    public static Task<IdGenerator> InitializeAsync(IdGeneratorOptions options, CancellationToken stop = default) =>
        InitializeAsync(token => SnowflakeIdGenerator.CreateAsync(options, token), stop);

    // 测试可创建不连接 ZooKeeper 的内部实现，仍经过相同的单例生命周期。
    internal static async Task<IdGenerator> InitializeAsync(
        Func<CancellationToken, Task<SnowflakeIdGenerator>> create, CancellationToken stop = default)
    {
        stop.ThrowIfCancellationRequested();
        if (instance is not null)
        {
            throw new InvalidOperationException("The process ID generator is already initialized.");
        }

        var generator = await create(stop).ConfigureAwait(false);
        try
        {
            stop.ThrowIfCancellationRequested();
            var initialized = new IdGenerator(generator);
            Volatile.Write(ref instance, initialized);
            return initialized;
        }
        catch
        {
            await generator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!ReferenceEquals(instance, this))
        {
            return;
        }

        Volatile.Write(ref instance, null);
        await generator.DisposeAsync().ConfigureAwait(false);
    }
}
