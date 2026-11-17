using System.Reflection;
using TGame;
using TGame.Server.Engine;

internal static class SinglePointChecks
{
    internal static void CheckBeforeIdInitialization()
    {
        using var node = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Router), 1),
            TestNodes.DefaultGroup, new RpcDispatcher());
        if (node.SinglePoint is not null || node.Ready)
        {
            throw new Exception("Engine construction must not create a SinglePoint or become ready.");
        }
    }

    public static async Task RunAsync()
    {
        var registry = new SinglePointRegistry([]);
        foreach (var role in Enum.GetValues<ProcessType>())
        {
            var singlePoint = registry.Create(role);
            if (singlePoint.GetType() != typeof(ServerSinglePoint))
            {
                throw new Exception("A role without a business implementation must use the engine default SinglePoint.");
            }

            if (!IdText.IsValid(singlePoint.EntityId) || singlePoint.LocalEntityIds.Any())
            {
                throw new Exception("A new SinglePoint must own its identity without registering it yet.");
            }

            using var node = TestNodes.CreateIsolated(role);
            await node.InitializeSinglePointAsync(registry, CancellationToken.None);
            singlePoint = node.SinglePoint!;
            if (!node.Ready ||
                !singlePoint.Initialized ||
                !singlePoint.ContainsEntity(singlePoint.EntityId) ||
                !singlePoint.LocalEntityIds.SequenceEqual([singlePoint.EntityId]))
            {
                throw new Exception("The default SinglePoint must register its own identity after initialization.");
            }
        }

        Expect<ArgumentOutOfRangeException>(() => registry.Create((ProcessType)int.MaxValue));
        Expect<InvalidOperationException>(() => new SinglePointRegistry([typeof(UnmarkedSinglePoint)]));
        Expect<InvalidOperationException>(() => new SinglePointRegistry([typeof(NotASinglePoint)]));
        Expect<InvalidOperationException>(() => new SinglePointRegistry([typeof(NoDefaultConstructor)]));
        Expect<InvalidOperationException>(() => new SinglePointRegistry([typeof(WaitingSinglePoint), typeof(DuplicateSinglePoint)]));

        var failing = new SinglePointRegistry([typeof(ConstructorFailureSinglePoint)]);
        Expect<TargetInvocationException>(() => failing.Create(ProcessType.Gate));

        Console.WriteLine("PASS optional SinglePoint defaults, owned identity, invalid and duplicate registration, constructor failure.");
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

        throw new Exception($"Expected {typeof(T).Name}; invalid registration must not fall back to a default SinglePoint.");
    }
}

sealed class UnmarkedSinglePoint : ServerSinglePoint;

[SinglePointFor(ProcessType.Gate)]
sealed class NotASinglePoint;

[SinglePointFor(ProcessType.Gate)]
sealed class NoDefaultConstructor(int value) : ServerSinglePoint
{
    public int Value { get; } = value;
}

[SinglePointFor(ProcessType.Gate)]
sealed class DuplicateSinglePoint : ServerSinglePoint;

[SinglePointFor(ProcessType.Gate)]
sealed class ConstructorFailureSinglePoint : ServerSinglePoint
{
    public ConstructorFailureSinglePoint()
    {
        throw new InvalidOperationException("Business constructor failed.");
    }
}

[SinglePointFor(ProcessType.Gate)]
sealed class InitializationFailureSinglePoint : ServerSinglePoint, IDisposable
{
    public bool Disposed { get; private set; }

    protected override Task InitializeRoleAsync(CancellationToken stop)
    {
        throw new InvalidDataException("Business initialization failed.");
    }

    public void Dispose()
    {
        Disposed = true;
    }
}
