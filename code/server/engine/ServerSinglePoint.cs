using System.Collections.Concurrent;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace TGame.Server.Engine;

public class ServerSinglePoint
{
    private readonly ConcurrentDictionary<string, byte> localEntities = new();

    public string EntityId { get; } = IdGenerator.GenId();
    public IEnumerable<string> LocalEntityIds => localEntities.Keys;

    public bool Initialized => Volatile.Read(ref initialized);
    private bool initialized;

    public async Task InitializeAsync(ServerNode node, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(node);
        var role = node.Role;
        GameLog.Info(ServerLogEvents.SinglePointInit, role, entity: EntityId, detail: GetType().Name);

        try
        {
            stop.ThrowIfCancellationRequested();
            await InitializeRoleAsync(stop);
            stop.ThrowIfCancellationRequested();
            GameLog.Info(ServerLogEvents.InitStepCompleted, role, entity: EntityId,
                detail: $"{GetType().Name} 业务初始化完成");

            // 业务初始化成功后才登记实体，并等待对端确认。
            RegisterSinglePoint();
            GameLog.Info(ServerLogEvents.InitStepCompleted, role, entity: EntityId, detail: "SinglePoint 本地实体登记完成");

            await node.RegisterEntitiesAsync(stop);
            stop.ThrowIfCancellationRequested();
            GameLog.Info(ServerLogEvents.InitStepCompleted, role, entity: EntityId, detail: "SinglePoint 实体注册阶段完成");

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

    public virtual Task<IMessage> HandleRequestAsync(EntityRpcContext context, Any payload, CancellationToken stop)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented,
            $"Unsupported SinglePoint request type {Any.GetTypeName(payload.TypeUrl)}."));
    }

    private void RegisterSinglePoint() => localEntities[EntityId] = 0;

    private void UnregisterSinglePoint() => localEntities.TryRemove(EntityId, out _);

    protected virtual Task InitializeRoleAsync(CancellationToken stop) => Task.CompletedTask;
}
