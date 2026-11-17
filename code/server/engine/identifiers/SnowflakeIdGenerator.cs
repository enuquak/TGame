using System.Globalization;
using org.apache.zookeeper;

namespace TGame.Server.Engine;

public sealed class SnowflakeIdGenerator : IIdGenerator, IAsyncDisposable
{
    private const int SequenceWaitMilliseconds = 1;
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly SnowflakeSequence sequence;
    private readonly IdSession session;
    private readonly ZooKeeper? zk;
    private bool disposed;

    internal SnowflakeIdGenerator(int workerId, TimeProvider clock, ZooKeeper? zk = null, IdSession? session = null)
    {
        WorkerId = workerId;
        this.clock = clock;
        this.zk = zk;
        this.session = session ?? new IdSession();
        sequence = new SnowflakeSequence(workerId);
    }

    public int WorkerId { get; }
    public Task LeaseLost => session.Lost.Task;

    public static async Task<SnowflakeIdGenerator> CreateAsync(IdGeneratorOptions options, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var session = new IdSession();
        var zk = new ZooKeeper(options.ZooKeeperEndpoint, options.SessionTimeoutMilliseconds, session);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(options.ConnectTimeoutMilliseconds);
        var token = timeout.Token;
        try
        {
            await session.Connected.Task.WaitAsync(token).ConfigureAwait(false);
            var path = "";
            foreach (var segment in options.WorkerRootPath[1..].Split('/'))
            {
                path += "/" + segment;
                try
                {
                    await zk.createAsync(path, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT).WaitAsync(token).ConfigureAwait(false);
                }
                catch (KeeperException.NodeExistsException)
                {
                }
            }

            for (int workerId = 0; workerId <= SnowflakeSequence.MaxWorkerId; workerId++)
            {
                token.ThrowIfCancellationRequested();
                var workerPath = options.WorkerRootPath + "/" + workerId.ToString(CultureInfo.InvariantCulture);
                try
                {
                    await zk.createAsync(workerPath, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(token).ConfigureAwait(false);
                }
                catch (KeeperException.NodeExistsException)
                {
                    continue;
                }

                // Start in a later millisecond than allocation, including very fast same-clock restarts.
                long allocatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= allocatedAt)
                {
                    await Task.Delay(SequenceWaitMilliseconds, token).ConfigureAwait(false);
                }

                session.CheckActive();
                return new SnowflakeIdGenerator(workerId, TimeProvider.System, zk, session);
            }

            throw new InvalidOperationException("All Snowflake worker IDs are occupied.");
        }
        catch
        {
            await zk.closeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public string GenId()
    {
        while (true)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (zk is not null)
                {
                    session.CheckActive();
                }

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

        if (zk is not null)
        {
            await zk.closeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class IdSession : Watcher
{
    private volatile bool connected;
    private volatile bool failed;
    internal TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Lost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task process(WatchedEvent evt)
    {
        StateChanged(evt.getState());
        return Task.CompletedTask;
    }

    internal void StateChanged(Event.KeeperState state)
    {
        if (state == Event.KeeperState.SyncConnected && !failed)
        {
            connected = true;
            Connected.TrySetResult();
            return;
        }

        if (state is Event.KeeperState.Expired or Event.KeeperState.AuthFailed ||
            state == Event.KeeperState.Disconnected && connected)
        {
            failed = true;
            connected = false;
            Connected.TrySetResult();
            Lost.TrySetResult();
        }
    }

    internal void CheckActive()
    {
        if (!connected || failed)
        {
            throw new InvalidOperationException("The Snowflake ZooKeeper session was lost; restart this process to acquire a new worker ID.");
        }
    }
}
