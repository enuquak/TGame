using System.Collections.Concurrent;

namespace TGame.Server.Engine;

public class ServerSinglePoint
{
    private readonly ConcurrentDictionary<string, byte> localEntities = new();

    public string EntityId { get; } = IdGenerator.GenId();
    public IEnumerable<string> LocalEntityIds => localEntities.Keys;

    public bool Initialized => Volatile.Read(ref initialized);
    private bool initialized;

    public async Task InitializeAsync(string role,
        Func<CancellationToken, Task> registerEntities, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(registerEntities);
        GameLog.Info(ServerLogEvents.SinglePointInit, role, entity: EntityId, detail: GetType().Name);

        try
        {
            stop.ThrowIfCancellationRequested();
            await InitializeRoleAsync(stop);
            stop.ThrowIfCancellationRequested();

            // 业务初始化成功后才登记实体，并等待对端确认。
            RegisterSinglePoint();
            await registerEntities(stop);
            stop.ThrowIfCancellationRequested();
            Volatile.Write(ref initialized, true);
            GameLog.Info(ServerLogEvents.SinglePointReady, role, entity: EntityId, detail: GetType().Name);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            UnregisterSinglePoint();
            throw;
        }
        catch (Exception error)
        {
            UnregisterSinglePoint();
            GameLog.Error(ServerLogEvents.SinglePointFailed, role, entity: EntityId, detail: error.Message);
            throw;
        }
    }

    public bool ContainsEntity(string entityId) => localEntities.ContainsKey(entityId);

    private void RegisterSinglePoint() => localEntities[EntityId] = 0;

    private void UnregisterSinglePoint() => localEntities.TryRemove(EntityId, out _);

    protected virtual Task InitializeRoleAsync(CancellationToken stop) => Task.CompletedTask;
}
