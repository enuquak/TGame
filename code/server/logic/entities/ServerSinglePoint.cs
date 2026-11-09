using TGame.Server.Model;
using TGame.Server;

namespace TGame.Server.Logic;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class SinglePointForAttribute(ProcessType role) : Attribute
{
    public ProcessType Role { get; } = role;
}

internal abstract class ServerSinglePoint
{
    public SinglePointState State { get; } = new();

    public bool Initialized => Volatile.Read(ref initialized);
    private bool initialized;

    public async Task InitializeAsync(string role, Action<string> registerEntity, Task firstLinks, CancellationToken stop)
    {
        TraceLog.Event(ServerLogEvents.SinglePointInit, role, entity: State.EntityId, detail: GetType().Name);

        try
        {
            stop.ThrowIfCancellationRequested();
            registerEntity(State.EntityId);
            if (Topology.Entities.TryGetValue(role, out var probeEntity))
            {
                registerEntity(probeEntity);
            }

            await firstLinks.WaitAsync(stop);
            await InitializeRoleAsync(stop);
            stop.ThrowIfCancellationRequested();
            Volatile.Write(ref initialized, true);
            TraceLog.Event(ServerLogEvents.SinglePointReady, role, entity: State.EntityId, detail: GetType().Name);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            TraceLog.Event(ServerLogEvents.SinglePointFailed, role, entity: State.EntityId, detail: error.Message);
            throw;
        }
    }

    protected virtual Task InitializeRoleAsync(CancellationToken stop) => Task.CompletedTask;
}
