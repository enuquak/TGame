using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame.Transport;
using Enum = System.Enum;

namespace TGame.Server.Engine;

public partial class ServerNode
{
    private readonly Dictionary<string, string> peerSinglePoints = new(StringComparer.Ordinal);

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
            RequestId = IdGenerator.GenId(),
            SourceProcess = Name,
            DeadlineUnixMs = DateTimeOffset.UtcNow.Add(PeerLink.RequestTimeout).ToUnixTimeMilliseconds(),
            Payload = Any.Pack(request)
        };
    }

    private async Task<TReply> CallRoutedAsync<TReply>(RpcRequest request, CancellationToken stop)
        where TReply : IMessage<TReply>, new()
    {
        stop.ThrowIfCancellationRequested();
        var reply = await RouteReplyAsync(request, null, stop);
        return PeerLink.ReadReply<TReply>(reply);
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
            GameLog.Error(ServerLogEvents.EntityRpcFailed, Role, peer, request.RequestId, request.EntityId, detail: error.ToString());
            return ErrorReply(request.RequestId, StatusCode.Internal, "Entity RPC handler failed.");
        }
    }

    private async Task<RpcReply> RouteRequestAsync(RpcRequest request, string? peer, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        PeerLink.ValidateEnvelope(request.RequestId, request.Payload);
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

            if (singlePoint is RouterServerSinglePoint router)
            {
                request = request.Clone();
                request.EntityId = router.SelectSinglePoint(type);
            }
        }
        else if (request.TargetCase != RpcRequest.TargetOneofCase.EntityId || !IdText.IsValid(request.EntityId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid RPC target entity."));
        }

        var point = singlePoint;
        if (request.TargetCase == RpcRequest.TargetOneofCase.EntityId && point?.ContainsEntity(request.EntityId) == true)
        {
            if (Role == nameof(ProcessType.Gate))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Gate entities are not RPC targets."));
            }

            if (!Ready)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Target entity is not ready."));
            }

            var context = new EntityRpcContext(request.RequestId, request.SourceProcess, request.EntityId, peer);
            var response = await point.HandleRequestAsync(context, request.Payload, stop);
            return new RpcReply { RequestId = request.RequestId, Payload = Any.Pack(response) };
        }

        var next = SelectNextLink(request, peer);
        return await next.CallAsync(request, stop);
    }

    private void ValidateSource(string source, string? peer)
    {
        if (!ServerProcess.TryParse(source, out _))
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

        if (source == peer || ServerProcess.Parse(peer).Type == ProcessType.Router)
        {
            return;
        }

        bool gateRelay = Role == nameof(ProcessType.Router) &&
            ServerProcess.Parse(source).Type == ProcessType.Gate &&
            ServerProcess.Parse(peer).Type is ProcessType.Login or ProcessType.Base &&
            Topology.Dials(ServerProcess.Parse(source).Type, ServerProcess.Parse(peer).Type);
        if (!gateRelay)
        {
            throw new InvalidDataException("Invalid forwarded RPC source.");
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

                if (singlePoint is RouterServerSinglePoint router)
                {
                    var owner = router.FindRemoteEntityOwner(request.EntityId);
                    if (owner is not null && links.TryGetValue(owner, out var target))
                    {
                        return target;
                    }

                    throw new RpcException(new Status(StatusCode.NotFound, "Target entity is not registered."));
                }
            }

            // A Router already chose the destination; never bounce a missing entity back to it.
            if (peer is not null && ServerProcess.Parse(peer).Type == ProcessType.Router)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Target entity is not hosted here."));
            }

            var candidates = new List<PeerLink>();
            foreach (var entry in links)
            {
                var type = ServerProcess.Parse(entry.Key).Type;
                bool usable = Role == nameof(ProcessType.Gate)
                    ? type is ProcessType.Login or ProcessType.Base
                    : type == ProcessType.Router;
                bool registered = Role == nameof(ProcessType.Gate)
                    ? peerSinglePoints.ContainsKey(entry.Key)
                    : registeredLinks.Contains(entry.Value);
                if (usable && registered && entry.Key != peer)
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

    private Frame CreateHello() => new() { Hello = Name };

    private static RpcReply ErrorReply(string requestId, StatusCode status, string detail) => new()
    {
        RequestId = requestId,
        Error = new RpcError { StatusCode = (int)status, Detail = detail }
    };
}
