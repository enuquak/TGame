namespace TGame.Server.Engine;

public class ServerSinglePoint
{
    public SinglePointState State { get; private set; } = null!;

    public bool Initialized => Volatile.Read(ref initialized);
    private bool initialized;

    public async Task InitializeAsync(string role, SinglePointState state,
        Func<CancellationToken, Task> registerEntities, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(registerEntities);
        State = state;
        TraceLog.Event(ServerLogEvents.SinglePointInit, role, entity: State.EntityId, detail: GetType().Name);

        try
        {
            stop.ThrowIfCancellationRequested();
            await InitializeRoleAsync(stop);
            stop.ThrowIfCancellationRequested();

            // 业务初始化成功后才登记实体，并等待对端确认。
            State.RegisterSinglePoint();
            await registerEntities(stop);
            stop.ThrowIfCancellationRequested();
            Volatile.Write(ref initialized, true);
            TraceLog.Event(ServerLogEvents.SinglePointReady, role, entity: State.EntityId, detail: GetType().Name);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            State.UnregisterSinglePoint();
            throw;
        }
        catch (Exception error)
        {
            State.UnregisterSinglePoint();
            TraceLog.Event(ServerLogEvents.SinglePointFailed, role, entity: State.EntityId, detail: error.Message);
            throw;
        }
    }

    protected virtual Task InitializeRoleAsync(CancellationToken stop) => Task.CompletedTask;
}
