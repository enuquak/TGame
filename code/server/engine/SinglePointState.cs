using System.Collections.Concurrent;

namespace TGame.Server.Engine;

public sealed class SinglePointState
{
    private readonly ConcurrentDictionary<string, byte> localEntities = new();

    public string EntityId { get; }
    public IEnumerable<string> LocalEntityIds => localEntities.Keys;

    public SinglePointState(IIdGenerator idGenerator)
    {
        EntityId = idGenerator.GenId();
    }

    internal void RegisterSinglePoint() => localEntities[EntityId] = 0;

    internal void UnregisterSinglePoint() => localEntities.TryRemove(EntityId, out _);

    public bool ContainsEntity(string entityId) => localEntities.ContainsKey(entityId);
}
