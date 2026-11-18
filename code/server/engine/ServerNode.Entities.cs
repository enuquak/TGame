using Google.Protobuf;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server.Engine;

public partial class ServerNode
{
    private readonly HashSet<PeerLink> registeredLinks = [];
    private readonly TaskCompletionSource entitiesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource registrationChanges = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                return HasRequiredLinks(requireRegistration: true);
            }
        }
    }

    internal async Task RegisterEntitiesAsync(CancellationToken stop)
    {
        if (Role == nameof(ProcessType.Gate))
        {
            return;
        }

        entitiesReady.TrySetResult();
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            Task changed;
            lock (linkChanges)
            {
                if (HasRequiredLinks(requireRegistration: true))
                {
                    return;
                }

                changed = registrationChanges.Task;
            }

            await changed.WaitAsync(stop);
        }
    }

    private async Task RegisterLinkAsync(PeerLink link, CancellationToken stop)
    {
        var point = singlePoint ?? throw new InvalidOperationException("SinglePoint has not been created.");
        var request = new RegisterEntitiesRequest { SinglePointId = point.EntityId };
        request.EntityIds.Add(point.LocalEntityIds);
        // 注册可等待对端初始化，取消和断线仍会终止等待。
        await link.CallAsync<RegisterEntitiesReply>(request, IdGenerator.GenId(), stop, timeout: Timeout.InfiniteTimeSpan);
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
            PeerLink.ValidateEnvelope(request.RequestId, request.Payload);
            if (request.TargetCase != RpcRequest.TargetOneofCase.None || request.SourceProcess.Length != 0)
            {
                throw new InvalidDataException("Entity registration must come directly from the connected process.");
            }

            await RegisterPeerEntitiesAsync(link, request.Payload.Unpack<RegisterEntitiesRequest>(), stop);
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

    private async Task RegisterPeerEntitiesAsync(PeerLink link, RegisterEntitiesRequest request, CancellationToken stop)
    {
        var peer = link.Peer;
        var entities = request.EntityIds.ToArray();
        if (ServerProcess.Parse(peer).Type == ProcessType.Gate ||
            !IdText.IsValid(request.SinglePointId) ||
            !entities.Contains(request.SinglePointId) ||
            entities.Any(entity => !IdText.IsValid(entity)) ||
            entities.Distinct().Count() != entities.Length)
        {
            throw new InvalidDataException($"Invalid entity registration from {peer}.");
        }

        RouterServerSinglePoint? router = null;
        if (Role == nameof(ProcessType.Router))
        {
            // 注册可能先于本地 SinglePoint 创建到达，确认前必须交由实例持有归属。
            router = (RouterServerSinglePoint)await singlePointCreated.Task.WaitAsync(stop);
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
            if (localCollision)
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"Duplicate entity from {peer}."));
            }

            router?.RegisterEntities(peer, request.SinglePointId, entities, link.Id);
            peerSinglePoints.Add(peer, request.SinglePointId);
        }
    }
}
