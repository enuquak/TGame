namespace TGame.Server.Engine;

public class ServerSinglePoint
{
    public SinglePointState State { get; private set; } = null!;

    public bool Initialized => Volatile.Read(ref initialized);
    private bool initialized;

    public async Task InitializeAsync(string role, SinglePointState state, CancellationToken stop)
    {
        State = state;
        TraceLog.Event(ServerLogEvents.SinglePointInit, role, entity: State.EntityId, detail: GetType().Name);

        try
        {
            stop.ThrowIfCancellationRequested();
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
