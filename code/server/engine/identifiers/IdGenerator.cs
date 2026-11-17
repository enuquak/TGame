namespace TGame.Server.Engine;

public sealed class IdGenerator : IAsyncDisposable
{
    private static IdGenerator? instance;
    private static readonly SemaphoreSlim lifecycle = new(1);
    private readonly SnowflakeIdGenerator generator;

    private IdGenerator(SnowflakeIdGenerator generator)
    {
        this.generator = generator;
    }

    private static SnowflakeIdGenerator Current => Volatile.Read(ref instance)?.generator
        ?? throw new InvalidOperationException("Initialize the process ID generator before use.");

    public static string GenId() => Current.GenId();

    public static Task LeaseLost => Current.LeaseLost;

    // 返回生命周期对象供宿主 await using，调用方无需持有或传递生成器。
    public static Task<IdGenerator> InitializeAsync(IdGeneratorOptions options, CancellationToken stop = default) =>
        InitializeAsync(token => SnowflakeIdGenerator.CreateAsync(options, token), stop);

    // 测试可创建不连接 ZooKeeper 的内部实现，仍经过相同的单例生命周期。
    internal static async Task<IdGenerator> InitializeAsync(
        Func<CancellationToken, Task<SnowflakeIdGenerator>> create, CancellationToken stop = default)
    {
        await lifecycle.WaitAsync(stop).ConfigureAwait(false);
        try
        {
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
        finally
        {
            lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            // 旧生命周期的重复释放不能影响之后重新初始化的实例。
            if (!ReferenceEquals(instance, this))
            {
                return;
            }

            Volatile.Write(ref instance, null);
            await generator.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }
}
