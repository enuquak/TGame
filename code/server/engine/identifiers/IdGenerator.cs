namespace TGame.Server.Engine;

public sealed class IdGenerator : IAsyncDisposable
{
    private bool disposed;
    private SnowflakeIdGenerator? generator;
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly Func<CancellationToken, Task<SnowflakeIdGenerator>> create;

    public IdGenerator(IdGeneratorOptions options, ZooKeeperProxy zooKeeper)
        : this(stop => SnowflakeIdGenerator.CreateAsync(options, zooKeeper, stop))
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(zooKeeper);
    }

    internal IdGenerator(Func<CancellationToken, Task<SnowflakeIdGenerator>> create)
    {
        this.create = create;
    }

    private SnowflakeIdGenerator Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Volatile.Read(ref generator)
                ?? throw new InvalidOperationException("Initialize the process ID generator before use.");
        }
    }

    public string GenId() => Current.GenId();

    public Task LeaseLost => Current.LeaseLost;

    public async Task InitializeAsync(CancellationToken stop = default)
    {
        await lifecycle.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (generator is not null)
            {
                throw new InvalidOperationException("The process ID generator is already initialized.");
            }

            var initialized = await create(stop).ConfigureAwait(false);
            try
            {
                stop.ThrowIfCancellationRequested();
                Volatile.Write(ref generator, initialized);
            }
            catch
            {
                await initialized.DisposeAsync().ConfigureAwait(false);
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
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (generator is not null)
            {
                await generator.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }
}
