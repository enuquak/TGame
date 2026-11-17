using System.Collections.Concurrent;
using System.Globalization;
using org.apache.zookeeper;
using TGame;
using TGame.Server.Engine;

internal static class IdGeneratorChecks
{
    internal static async Task RunAsync()
    {
        const int WorkerId = 42;
        const long Timestamp = 100;
        var sequence = new SnowflakeSequence(WorkerId);
        var ids = new HashSet<long>();
        for (int i = 0; i <= SnowflakeSequence.MaxSequence; i++)
        {
            long id = sequence.TryGenId(Timestamp)!.Value;
            Check(ids.Add(id), "same-millisecond IDs must be unique");
            Check((id >> SnowflakeSequence.SequenceBits & SnowflakeSequence.MaxWorkerId) == WorkerId, "worker bits");
        }

        Check(sequence.TryGenId(Timestamp) is null, "exhaustion must wait, not wrap");
        Check(sequence.TryGenId(Timestamp + 1) > ids.Max(), "next millisecond resets the sequence");
        Expect<InvalidOperationException>(() => sequence.TryGenId(Timestamp));
        Expect<ArgumentOutOfRangeException>(() => new SnowflakeSequence(-1));
        Expect<ArgumentOutOfRangeException>(() => new SnowflakeSequence(SnowflakeSequence.MaxWorkerId + 1));
        Expect<InvalidOperationException>(() => sequence.TryGenId(SnowflakeSequence.MaxTimestamp + 1));
        Expect<InvalidOperationException>(() => new SnowflakeSequence(0).TryGenId(-1));
        Check(new SnowflakeSequence(SnowflakeSequence.MaxWorkerId).TryGenId(SnowflakeSequence.MaxTimestamp) > 0, "signed long boundary");

        var clock = new ManualClock(Timestamp);
        await using var idGenerator = new SnowflakeIdGenerator(WorkerId, clock);
        string first = idGenerator.GenId();
        clock.Timestamp--;
        Expect<InvalidOperationException>(() => idGenerator.GenId());
        clock.Timestamp++;
        long expectedNext = long.Parse(first, CultureInfo.InvariantCulture) + 1;
        Check(idGenerator.GenId() == expectedNext.ToString(CultureInfo.InvariantCulture), "rollback must not reset the sequence");

        await using var concurrent = new SnowflakeIdGenerator(WorkerId, TimeProvider.System);
        var generated = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        Parallel.For(0, 50000, _ => Check(generated.TryAdd(concurrent.GenId(), 0), "concurrent duplicate ID"));
        await concurrent.DisposeAsync();
        Expect<ObjectDisposedException>(() => concurrent.GenId());

        var session = new ZooKeeperSession();
        session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
        session.CheckActive();
        session.StateChanged(Watcher.Event.KeeperState.Disconnected);
        Check(session.Lost.Task.IsCompletedSuccessfully, "disconnection must notify the host");
        Expect<InvalidOperationException>(session.CheckActive);
        session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
        Expect<InvalidOperationException>(session.CheckActive);

        foreach (var invalid in new[] { "", "0", "01", "-1", "+1", " 1", "1 ", "9223372036854775808", "body-1" })
        {
            Check(!IdText.IsValid(invalid), "noncanonical ID accepted: " + invalid);
        }

        Check(IdText.IsValid(first), "generated ID rejected");
        Console.WriteLine("PASS Snowflake layout, sequence exhaustion, rollback, 50000 concurrent IDs, session loss, disposal and ID validation.");
    }

    internal static async Task RunZooKeeperAsync(string configPath)
    {
        var configured = IdGeneratorOptions.Load(configPath);
        var zooKeeperOptions = ZooKeeperProxyChecks.LoadOptions();
        var root = configured.WorkerRootPath + "-checks/" + IdGenerator.GenId();
        var options = new IdGeneratorOptions
        {
            WorkerRootPath = root
        };

        var generators = new List<SnowflakeIdGenerator>();
        var session = new ZooKeeperSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stop = timeout.Token;
        await using var proxy = await ZooKeeperProxy.InitializeAsync(zooKeeperOptions, stop);
        var zk = new ZooKeeper(zooKeeperOptions.Endpoint, zooKeeperOptions.SessionTimeoutMilliseconds, session);
        try
        {
            await session.Connected.Task.WaitAsync(stop);
            var tasks = Enumerable.Range(0, 4).Select(_ => SnowflakeIdGenerator.CreateAsync(options, stop)).ToArray();
            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                foreach (var task in tasks)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        generators.Add(task.Result);
                    }
                }
            }

            Check(generators.Select(g => g.WorkerId).Distinct().Count() == generators.Count, "ZooKeeper assigned duplicate workers");
            Check(generators.All(g => ReferenceEquals(g.LeaseLost, proxy.SessionLost)), "Workers must share the proxy session");
            var ids = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            Parallel.For(0, 20000, i => Check(ids.TryAdd(generators[i % generators.Count].GenId(), 0), "cross-worker duplicate"));

            var first = generators.Single(g => g.WorkerId == 0);
            await first.DisposeAsync();
            var replacement = await SnowflakeIdGenerator.CreateAsync(options, stop);
            generators.Add(replacement);
            Check(replacement.WorkerId == first.WorkerId, "released worker was not reused");
            Check(ids.TryAdd(replacement.GenId(), 0), "reused worker duplicated an earlier ID");
            Check(!proxy.SessionLost.IsCompleted, "Releasing a worker must preserve the shared session");
            await proxy.DisposeAsync();
            Check(replacement.LeaseLost.IsCompletedSuccessfully, "Proxy disposal must invalidate the worker lease");
            Expect<InvalidOperationException>(() => replacement.GenId());
            Console.WriteLine("PASS real ZooKeeper concurrent allocation, 20000 cross-worker IDs and worker reuse.");
        }
        finally
        {
            foreach (var generator in generators)
            {
                await generator.DisposeAsync();
            }

            if (await zk.existsAsync(root).WaitAsync(stop) is not null)
            {
                await zk.deleteAsync(root).WaitAsync(stop);
            }

            await zk.closeAsync();
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

    private sealed class ManualClock(long timestamp) : TimeProvider
    {
        internal long Timestamp = timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(SnowflakeSequence.EpochMilliseconds + Timestamp);
    }
}
