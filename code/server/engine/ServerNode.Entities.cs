using Google.Protobuf;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server.Engine;

public partial class ServerNode
{
    private readonly HashSet<PeerLink> registeredLinks = [];
    private readonly Dictionary<PeerLink, Task> registrations = [];
    private readonly Dictionary<string, string[]> peerEntities = new(StringComparer.Ordinal);

    private bool RegistrationsComplete
    {
        get
        {
            if (Role == nameof(ProcessType.Gate))
            {
                return true;
            }

            lock (linkChanges)
            {
                return expectedPeers.All(peer => links.TryGetValue(peer, out var link) && registeredLinks.Contains(link));
            }
        }
    }

    private async Task RegisterEntitiesAsync(CancellationToken stop)
    {
        if (Role == nameof(ProcessType.Gate))
        {
            return;
        }

        var point = singlePoint ?? throw new InvalidOperationException("SinglePoint has not been created.");
        Task[] pending;
        lock (linkChanges)
        {
            if (!expectedPeers.All(links.ContainsKey))
            {
                throw new IOException("Cannot register entities while an engine connection is missing.");
            }

            pending = links.Values.Select(link => EnsureRegistrationAsync(link, stop)).ToArray();
        }

        await Task.WhenAll(pending);
        stop.ThrowIfCancellationRequested();
        lock (linkChanges)
        {
            if (Role == nameof(ProcessType.Router))
            {
                singlePointIds[ProcessType.Router] = [point.EntityId];
            }
        }
    }

    private Task EnsureRegistrationAsync(PeerLink link, CancellationToken stop)
    {
        lock (linkChanges)
        {
            if (!links.TryGetValue(link.Peer, out var current) || current != link)
            {
                throw new IOException("Entity registration connection is no longer active.");
            }

            if (registrations.TryGetValue(link, out var pending))
            {
                return pending;
            }

            var registration = RegisterLinkAsync(link, stop);
            registrations.Add(link, registration);
            return registration;
        }
    }

    private async Task RegisterLinkAsync(PeerLink link, CancellationToken stop)
    {
        var point = singlePoint ?? throw new InvalidOperationException("SinglePoint has not been created.");
        var request = new RegisterEntitiesRequest { SinglePointId = point.EntityId };
        request.EntityIds.Add(point.LocalEntityIds);
        await link.CallAsync<RegisterEntitiesReply>(request, IdGenerator.GenId(), stop);
        stop.ThrowIfCancellationRequested();

        lock (linkChanges)
        {
            if (!links.TryGetValue(link.Peer, out var current) || current != link)
            {
                throw new IOException("Entity registration connection closed before acknowledgement.");
            }

            registeredLinks.Add(link);
            UpdateLinkReadiness();
        }
    }

    private async Task HandleRegistrationAsync(RpcRequest request, PeerLink link, CancellationToken stop)
    {
        try
        {
            RpcDispatcher.ValidateEnvelope(request.RequestId, request.Payload);
            if (request.TargetCase != RpcRequest.TargetOneofCase.None || request.SourceProcess.Length != 0)
            {
                throw new InvalidDataException("Entity registration must come directly from the connected process.");
            }

            RegisterPeerEntities(link, request.Payload.Unpack<RegisterEntitiesRequest>());
        }
        catch (RpcException error)
        {
            var reply = ErrorReply(request.RequestId, error.StatusCode, error.Status.Detail);
            await link.WriteAsync(new Frame { Reply = reply }, stop);
            return;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException)
        {
            var reply = ErrorReply(request.RequestId, StatusCode.InvalidArgument, error.Message);
            await link.WriteAsync(new Frame { Reply = reply }, stop);
            return;
        }

        await link.ReplyAsync(new RegisterEntitiesReply(), request.RequestId, stop);
    }

    private void RegisterPeerEntities(PeerLink link, RegisterEntitiesRequest request)
    {
        var peer = link.Peer;
        var entities = request.EntityIds.ToArray();
        if (PeerRole(peer) == nameof(ProcessType.Gate) ||
            !IdText.IsValid(request.SinglePointId) ||
            !entities.Contains(request.SinglePointId) ||
            entities.Any(entity => !IdText.IsValid(entity)) ||
            entities.Distinct().Count() != entities.Length)
        {
            throw new InvalidDataException($"Invalid entity registration from {peer}.");
        }

        lock (linkChanges)
        {
            if (!links.TryGetValue(peer, out var current) || current != link)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Entity registration connection is no longer active."));
            }

            if (peerSinglePoints.ContainsKey(peer))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, "Entities are already registered on this connection."));
            }

            var point = singlePoint;
            bool localCollision = point is not null && entities.Contains(point.EntityId);
            bool remoteCollision = Role == nameof(ProcessType.Router) && entities.Any(owners.ContainsKey);
            if (localCollision || remoteCollision)
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"Duplicate entity from {peer}."));
            }

            RegisterPeerSinglePoint(peer, request.SinglePointId);
            if (Role == nameof(ProcessType.Router))
            {
                peerEntities.Add(peer, entities);
                foreach (var entity in entities)
                {
                    owners[entity] = peer;
                    GameLog.Debug(ServerLogEvents.EntityRoute, Role, peer, entity: entity, link: link.Id);
                }
            }
        }
    }
}
