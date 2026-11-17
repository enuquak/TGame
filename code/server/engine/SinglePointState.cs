using System.Collections.Concurrent;

namespace TGame.Server.Engine;

public sealed class SinglePointState
{
    private readonly ConcurrentDictionary<string, byte> localEntities = new();

    public string EntityId { get; } = IdGenerator.GenId();
    public IEnumerable<string> LocalEntityIds => localEntities.Keys;

    internal void RegisterSinglePoint() => localEntities[EntityId] = 0;

    internal void UnregisterSinglePoint() => localEntities.TryRemove(EntityId, out _);

    public bool ContainsEntity(string entityId) => localEntities.ContainsKey(entityId);
}
