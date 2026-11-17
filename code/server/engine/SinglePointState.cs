using System.Collections.Concurrent;

namespace TGame.Server.Engine;

public sealed class SinglePointState
{
    private readonly ConcurrentDictionary<string, byte> localEntities = new();

    public string EntityId { get; }
    public IEnumerable<string> LocalEntityIds => localEntities.Keys;

    public SinglePointState(IIdGenerator idGenerator)
    {
        // 引擎阶段先登记身份，业务 SinglePoint 初始化时复用这份实体名单。
        EntityId = idGenerator.GenId();
        localEntities[EntityId] = 0;
    }

    public bool ContainsEntity(string entityId) => localEntities.ContainsKey(entityId);
}
