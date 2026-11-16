using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame.Transport;
using Enum = System.Enum;

namespace TGame.Server.Engine;

public partial class ServerNode
{
    private readonly Dictionary<string, string> peerSinglePoints = new(StringComparer.Ordinal);
    private readonly Dictionary<ProcessType, HashSet<string>> singlePointIds = [];

    public Task<TReply> CallEntityAsync<TReply>(string entityId, IMessage request, CancellationToken stop)
        where TReply : IMessage<TReply>, new()
    {
        if (!IdText.IsValid(entityId))
        {
            throw new ArgumentException("A canonical entity ID is required.", nameof(entityId));
        }

        var envelope = CreateRequest(request);
        envelope.EntityId = entityId;
        return CallRoutedAsync<TReply>(envelope, stop);
    }

    public Task<TReply> CallSinglePointAsync<TReply>(ProcessType processType, IMessage request, CancellationToken stop)
        where TReply : IMessage<TReply>, new()
    {
        if (!Enum.IsDefined(processType))
        {
            throw new ArgumentOutOfRangeException(nameof(processType));
        }

        var envelope = CreateRequest(request);
        envelope.SinglePointType = processType.ToString();
        return CallRoutedAsync<TReply>(envelope, stop);
    }

    private RpcRequest CreateRequest(IMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RpcRequest
        {
            RequestId = IdGenerator.GenId().ToString(CultureInfo.InvariantCulture),
            SourceProcess = Name,
            DeadlineUnixMs = DateTimeOffset.UtcNow.Add(PeerLink.RequestTimeout).ToUnixTimeMilliseconds(),
            Payload = Any.Pack(request)
        };
    }

    private async Task<TReply> CallRoutedAsync<TReply>(RpcRequest request, CancellationToken stop)
        where TReply : IMessage<TReply>, new()
    {
        stop.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(PeerLink.RequestTimeout);
        try
        {
            var reply = await RouteReplyAsync(request, null, deadline.Token).WaitAsync(deadline.Token);
            return PeerLink.ReadReply<TReply>(reply);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Entity RPC timed out.");
        }
    }

    private async Task<RpcReply> RouteReplyAsync(RpcRequest request, string? peer, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (request.DeadlineUnixMs <= now)
            {
                return ErrorReply(request.RequestId, StatusCode.DeadlineExceeded, "Entity RPC deadline expired.");
            }

            long remaining = request.DeadlineUnixMs - now;
            deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(remaining, PeerLink.RequestTimeout.TotalMilliseconds)));
            return await RouteRequestAsync(request, peer, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (RpcException error)
        {
            return ErrorReply(request.RequestId, error.StatusCode, error.Status.Detail);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return ErrorReply(request.RequestId, StatusCode.DeadlineExceeded, "Entity RPC deadline expired.");
        }
        catch (TimeoutException)
        {
            return ErrorReply(request.RequestId, StatusCode.DeadlineExceeded, "Entity RPC timed out.");
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException)
        {
            return ErrorReply(request.RequestId, StatusCode.InvalidArgument, error.Message);
        }
        catch (IOException)
        {
            return ErrorReply(request.RequestId, StatusCode.Unavailable, "Entity RPC connection closed.");
        }
        catch (Exception error)
        {
            TraceLog.Event(ServerLogEvents.EntityRpcFailed, Role, peer, request.RequestId, request.EntityId, detail: error.ToString());
            return ErrorReply(request.RequestId, StatusCode.Internal, "Entity RPC handler failed.");
        }
    }

    private async Task<RpcReply> RouteRequestAsync(RpcRequest request, string? peer, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        RpcDispatcher.ValidateEnvelope(request.RequestId, request.Payload);
        ValidateSource(request.SourceProcess, peer);

        if (request.TargetCase == RpcRequest.TargetOneofCase.SinglePointType)
        {
            if (!Enum.TryParse<ProcessType>(request.SinglePointType, out var type) ||
                !Enum.IsDefined(type) ||
                type.ToString() != request.SinglePointType ||
                type == ProcessType.Gate)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid SinglePoint target type."));
            }

            if (Role == nameof(ProcessType.Router))
            {
                request = request.Clone();
                request.EntityId = SelectSinglePoint(type);
            }
        }
        else if (request.TargetCase != RpcRequest.TargetOneofCase.EntityId || !IdText.IsValid(request.EntityId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid RPC target entity."));
        }

        if (request.TargetCase == RpcRequest.TargetOneofCase.EntityId && localEntities.ContainsKey(request.EntityId))
        {
            if (Role == nameof(ProcessType.Gate))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Gate entities are not RPC targets."));
            }

            if (!Ready)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Target entity is not ready."));
            }

            var response = await messages.DispatchEntityAsync(request, stop);
            return new RpcReply { RequestId = request.RequestId, Payload = Any.Pack(response) };
        }

        var next = SelectNextLink(request, peer);
        return await next.CallAsync(request, stop);
    }

    private void ValidateSource(string source, string? peer)
    {
        if (!group.Contains(source))
        {
            throw new InvalidDataException("Unknown RPC source process.");
        }

        if (peer is null)
        {
            if (source != Name)
            {
                throw new InvalidDataException("Invalid local RPC source.");
            }

            return;
        }

        if (source == peer || PeerRole(peer) == nameof(ProcessType.Router))
        {
            return;
        }

        bool gateRelay = Role == nameof(ProcessType.Router) &&
            PeerRole(source) == nameof(ProcessType.Gate) &&
            PeerRole(peer) is nameof(ProcessType.Login) or nameof(ProcessType.Base) &&
            Topology.Peers(group, peer).Contains(source);
        if (!gateRelay)
        {
            throw new InvalidDataException("Invalid forwarded RPC source.");
        }
    }

    private string SelectSinglePoint(ProcessType type)
    {
        lock (linkChanges)
        {
            if (!singlePointIds.TryGetValue(type, out var candidates) || candidates.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, $"No connected SinglePoint for {type}."));
            }

            return candidates.ElementAt(Random.Shared.Next(candidates.Count));
        }
    }

    private PeerLink SelectNextLink(RpcRequest request, string? peer)
    {
        lock (linkChanges)
        {
            if (request.TargetCase == RpcRequest.TargetOneofCase.EntityId)
            {
                if (peer is null || Role == nameof(ProcessType.Router))
                {
                    foreach (var entry in peerSinglePoints)
                    {
                        if (entry.Value == request.EntityId && entry.Key != peer && links.TryGetValue(entry.Key, out var direct))
                        {
                            return direct;
                        }
                    }
                }

                if (Role == nameof(ProcessType.Router))
                {
                    if (owners.TryGetValue(request.EntityId, out var owner) && links.TryGetValue(owner, out var target))
                    {
                        return target;
                    }

                    throw new RpcException(new Status(StatusCode.NotFound, "Target entity is not registered."));
                }
            }

            // A Router already chose the destination; never bounce a missing entity back to it.
            if (peer is not null && PeerRole(peer) == nameof(ProcessType.Router))
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Target entity is not hosted here."));
            }

            var candidates = new List<PeerLink>();
            foreach (var entry in links)
            {
                var type = group.GetProcess(entry.Key).Type;
                bool usable = Role == nameof(ProcessType.Gate)
                    ? type is ProcessType.Login or ProcessType.Base
                    : type == ProcessType.Router;
                if (usable && entry.Key != peer)
                {
                    candidates.Add(entry.Value);
                }
            }

            if (candidates.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "No connected RPC next hop."));
            }

            return candidates[Random.Shared.Next(candidates.Count)];
        }
    }

    private Frame CreateHello() => new()
    {
        Hello = Name,
        SinglePointId = Role == nameof(ProcessType.Gate) ? "" : State.EntityId
    };

    private void ValidateSinglePoint(string peer, string entityId, string[] entities)
    {
        if (PeerRole(peer) == nameof(ProcessType.Gate))
        {
            if (entityId.Length != 0)
            {
                throw new InvalidDataException("Gate must not advertise an RPC SinglePoint.");
            }

            return;
        }

        if (!IdText.IsValid(entityId) || (Role == nameof(ProcessType.Router) && !entities.Contains(entityId)))
        {
            throw new InvalidDataException("SinglePoint identity is missing from the peer's entity registration.");
        }
    }

    // Called with linkChanges held, alongside connection and entity ownership updates.
    private void RegisterPeerSinglePoint(string peer, string entityId)
    {
        if (entityId.Length == 0)
        {
            return;
        }

        peerSinglePoints.Add(peer, entityId);
        if (Role == nameof(ProcessType.Router))
        {
            var type = group.GetProcess(peer).Type;
            if (!singlePointIds.TryGetValue(type, out var candidates))
            {
                candidates = [];
                singlePointIds.Add(type, candidates);
            }

            candidates.Add(entityId);
        }
    }

    private void RemovePeerSinglePoint(string peer)
    {
        if (peerSinglePoints.Remove(peer, out var entityId) && Role == nameof(ProcessType.Router))
        {
            var type = group.GetProcess(peer).Type;
            singlePointIds[type].Remove(entityId);
        }
    }

    private static RpcReply ErrorReply(string requestId, StatusCode status, string detail) => new()
    {
        RequestId = requestId,
        Error = new RpcError { StatusCode = (int)status, Detail = detail }
    };
}
