using Grpc.Core;

namespace TGame.Server.Engine;

[SinglePointFor(ProcessType.Router)]
public sealed class RouterServerSinglePoint : ServerSinglePoint
{
    private readonly object entityRoutes = new();
    private readonly Dictionary<string, string> owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> peerEntities = new(StringComparer.Ordinal);
    private readonly Dictionary<ProcessType, HashSet<string>> singlePointIds = [];

    public bool IsRemoteEntityOwner(string process, string entityId)
    {
        lock (entityRoutes)
        {
            return owners.TryGetValue(entityId, out var owner) && owner == process;
        }
    }

    internal string? FindRemoteEntityOwner(string entityId)
    {
        lock (entityRoutes)
        {
            return owners.GetValueOrDefault(entityId);
        }
    }

    internal void RegisterEntities(string peer, string singlePointId, string[] entities, string linkId)
    {
        lock (entityRoutes)
        {
            if (peerEntities.ContainsKey(peer) || entities.Any(entity => entity == EntityId || owners.ContainsKey(entity)))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"Duplicate entity from {peer}."));
            }

            var type = ServerProcess.Parse(peer).Type;
            if (!singlePointIds.TryGetValue(type, out var candidates))
            {
                candidates = [];
                singlePointIds.Add(type, candidates);
            }

            candidates.Add(singlePointId);
            peerEntities.Add(peer, entities);
            foreach (var entity in entities)
            {
                owners.Add(entity, peer);
                GameLog.Debug(ServerLogEvents.EntityRoute, nameof(ProcessType.Router), peer, entity: entity, link: linkId);
            }
        }
    }

    internal void RemoveEntities(string peer, string singlePointId)
    {
        lock (entityRoutes)
        {
            if (!peerEntities.Remove(peer, out var entities))
            {
                return;
            }

            var type = ServerProcess.Parse(peer).Type;
            singlePointIds[type].Remove(singlePointId);
            foreach (var entity in entities)
            {
                owners.Remove(entity);
            }
        }
    }

    internal string SelectSinglePoint(ProcessType type)
    {
        lock (entityRoutes)
        {
            if (type == ProcessType.Router && Initialized)
            {
                return EntityId;
            }

            if (!singlePointIds.TryGetValue(type, out var candidates) || candidates.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, $"No connected SinglePoint for {type}."));
            }

            return candidates.ElementAt(Random.Shared.Next(candidates.Count));
        }
    }
}
