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
        Expect<InvalidOperationException>(() => IdGenerator.GenId());
        Expect<InvalidOperationException>(() => _ = IdGenerator.LeaseLost);

        await ExpectAsync<IOException>(() => IdGenerator.InitializeAsync(
            _ => Task.FromException<SnowflakeIdGenerator>(new IOException("Allocation failed.")), stop));
        Expect<InvalidOperationException>(() => IdGenerator.GenId());

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var abandoned = new SnowflakeIdGenerator(WorkerId, TimeProvider.System);
        await ExpectAsync<OperationCanceledException>(() => IdGenerator.InitializeAsync(_ =>
        {
            cancelled.Cancel();
            return Task.FromResult(abandoned);
        }, cancelled.Token));
        Expect<ObjectDisposedException>(() => abandoned.GenId());
        Expect<InvalidOperationException>(() => IdGenerator.GenId());

        var generator = new SnowflakeIdGenerator(WorkerId, TimeProvider.System);
        await using (var lifetime = await IdGenerator.InitializeAsync(_ => Task.FromResult(generator), stop))
        {
            await ExpectAsync<InvalidOperationException>(() => IdGenerator.InitializeAsync(_ => Task.FromResult(generator), stop));

            var generated = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            Parallel.For(0, ConcurrentIds, _ => Check(generated.TryAdd(IdGenerator.GenId(), 0), "Static entry produced duplicate IDs."));
        }

        Expect<ObjectDisposedException>(() => generator.GenId());
        Expect<InvalidOperationException>(() => IdGenerator.GenId());

        var session = new ZooKeeperSession();
        session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
        var next = new SnowflakeIdGenerator(WorkerId, TimeProvider.System, session: session);
        await using (var nextLifetime = await IdGenerator.InitializeAsync(_ => Task.FromResult(next), stop))
        {
            Check(IdText.IsValid(IdGenerator.GenId()), "The next lifecycle did not initialize its generator.");
            Check(ReferenceEquals(IdGenerator.LeaseLost, next.LeaseLost), "Singleton hid the active generator's lease supervision.");
            session.StateChanged(Watcher.Event.KeeperState.Disconnected);
            await IdGenerator.LeaseLost.WaitAsync(stop);
            Expect<InvalidOperationException>(() => IdGenerator.GenId());
            session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
            Expect<InvalidOperationException>(() => IdGenerator.GenId());
        }

        Expect<ObjectDisposedException>(() => next.GenId());
        Expect<InvalidOperationException>(() => IdGenerator.GenId());

        Console.WriteLine("PASS process ID singleton: initialization failure, cancellation cleanup, sequential initialization, 5000 IDs, lease supervision and disposal.");
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
