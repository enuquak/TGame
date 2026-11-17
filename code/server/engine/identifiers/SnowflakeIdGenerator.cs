using System.Globalization;

namespace TGame.Server.Engine;

internal sealed class SnowflakeIdGenerator : IAsyncDisposable
{
    private const int SequenceWaitMilliseconds = 1;
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly SnowflakeSequence sequence;
    private readonly ZooKeeperSession? session;
    private readonly ZooKeeperProxy.WorkerLease? worker;
    private bool disposed;

    internal SnowflakeIdGenerator(int workerId, TimeProvider clock, ZooKeeperSession? session = null)
    {
        WorkerId = workerId;
        this.clock = clock;
        this.session = session;
        sequence = new SnowflakeSequence(workerId);
        LeaseLost = session?.Lost.Task ?? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }

    private SnowflakeIdGenerator(ZooKeeperProxy.WorkerLease worker)
        : this(worker.WorkerId, TimeProvider.System, worker.Session)
    {
        this.worker = worker;
    }

    public int WorkerId { get; }
    public Task LeaseLost { get; }

    public static async Task<SnowflakeIdGenerator> CreateAsync(IdGeneratorOptions options, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var worker = await ZooKeeperProxy.Instance.AcquireWorkerAsync(options.WorkerRootPath, stop).ConfigureAwait(false);
        return new SnowflakeIdGenerator(worker);
    }

    public string GenId()
    {
        while (true)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                session?.CheckActive();

                long timestamp = clock.GetUtcNow().ToUnixTimeMilliseconds() - SnowflakeSequence.EpochMilliseconds;
                long? id = sequence.TryGenId(timestamp);
                if (id.HasValue)
                {
                    return id.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            Thread.Sleep(SequenceWaitMilliseconds);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        if (worker is not null)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }
}
