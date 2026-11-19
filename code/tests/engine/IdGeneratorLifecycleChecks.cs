using System.Collections.Concurrent;
using org.apache.zookeeper;
using TGame;
using TGame.Server.Engine;

internal static class IdGeneratorLifecycleChecks
{
    private const int WorkerId = 42;
    private const int ConcurrentIds = 5000;
    private const int TestTimeoutSeconds = 5;

    public static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));
        var stop = deadline.Token;
        await using var failed = new IdGenerator(_ => Task.FromException<SnowflakeIdGenerator>(new IOException("Allocation failed.")));
        Expect<InvalidOperationException>(() => failed.GenId());
        Expect<InvalidOperationException>(() => _ = failed.LeaseLost);
        await ExpectAsync<IOException>(() => failed.InitializeAsync(stop));
        Expect<InvalidOperationException>(() => failed.GenId());

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var abandoned = new SnowflakeIdGenerator(WorkerId, TimeProvider.System);
        await using var interrupted = new IdGenerator(_ =>
        {
            cancelled.Cancel();
            return Task.FromResult(abandoned);
        });
        await ExpectAsync<OperationCanceledException>(() => interrupted.InitializeAsync(cancelled.Token));
        Expect<ObjectDisposedException>(() => abandoned.GenId());
        Expect<InvalidOperationException>(() => interrupted.GenId());

        var invoked = false;
        await using var preCancelled = new IdGenerator(_ =>
        {
            invoked = true;
            return Task.FromResult(new SnowflakeIdGenerator(WorkerId, TimeProvider.System));
        });
        await ExpectAsync<OperationCanceledException>(() => preCancelled.InitializeAsync(cancelled.Token));
        Check(!invoked, "Pre-cancellation allocated a generator.");

        var generator = new SnowflakeIdGenerator(WorkerId, TimeProvider.System);
        var ids = new IdGenerator(_ => Task.FromResult(generator));
        await using (ids)
        {
            await ids.InitializeAsync(stop);
            await ExpectAsync<InvalidOperationException>(() => ids.InitializeAsync(stop));
            var generated = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            Parallel.For(0, ConcurrentIds, _ => Check(generated.TryAdd(ids.GenId(), 0), "Shared generator produced duplicate IDs."));
        }

        Expect<ObjectDisposedException>(() => generator.GenId());
        Expect<ObjectDisposedException>(() => ids.GenId());
        await ids.DisposeAsync();

        var session = new ZooKeeperSession();
        session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
        var next = new SnowflakeIdGenerator(WorkerId, TimeProvider.System, session: session);
        await using var nextIds = new IdGenerator(_ => Task.FromResult(next));
        await nextIds.InitializeAsync(stop);
        Check(IdText.IsValid(nextIds.GenId()), "The next instance did not initialize its generator.");
        Check(ReferenceEquals(nextIds.LeaseLost, next.LeaseLost), "The service hid the active generator's lease supervision.");
        await ids.DisposeAsync();
        Check(IdText.IsValid(nextIds.GenId()), "Disposing another instance invalidated the active generator.");
        session.StateChanged(Watcher.Event.KeeperState.Disconnected);
        await nextIds.LeaseLost.WaitAsync(stop);
        Expect<InvalidOperationException>(() => nextIds.GenId());
        session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
        Expect<InvalidOperationException>(() => nextIds.GenId());
        await nextIds.DisposeAsync();
        Expect<ObjectDisposedException>(() => next.GenId());
        Expect<ObjectDisposedException>(() => nextIds.GenId());

        Console.WriteLine("PASS process ID instances: initialization failure, cancellation cleanup, isolation, 5000 IDs, lease supervision and disposal.");
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
