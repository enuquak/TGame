using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server.Engine;

public sealed class RpcDispatcher
{
    private readonly Dictionary<string, Func<RpcRequest, string, PeerLink, CancellationToken, Task>> handlers = new();
    private readonly Dictionary<string, Func<EntityRpcContext, Any, CancellationToken, Task<IMessage>>> entityHandlers = new();

    public void RegisterEntity<TRequest, TReply>(Func<EntityRpcContext, TRequest, CancellationToken, Task<TReply>> handler)
        where TRequest : IMessage<TRequest>, new()
        where TReply : IMessage<TReply>, new()
    {
        ArgumentNullException.ThrowIfNull(handler);
        var type = new TRequest().Descriptor.FullName;
        if (!entityHandlers.TryAdd(type, async (context, payload, stop) =>
            await handler(context, payload.Unpack<TRequest>(), stop)))
        {
            throw new InvalidOperationException($"Entity RPC handler already registered for {type}.");
        }
    }

    internal Task<IMessage> DispatchEntityAsync(RpcRequest request, CancellationToken stop)
    {
        var type = Any.GetTypeName(request.Payload.TypeUrl);
        if (!entityHandlers.TryGetValue(type, out var handler))
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, $"Unknown entity RPC request type {type}."));
        }

        var context = new EntityRpcContext(request.RequestId, request.SourceProcess, request.EntityId);
        return handler(context, request.Payload, stop);
    }

    public void Register<TRequest>(Func<TRequest, string> requestId,
        Func<string, TRequest, PeerLink, CancellationToken, Task> handler)
        where TRequest : IMessage<TRequest>, new()
    {
        var type = new TRequest().Descriptor.FullName;
        if (!handlers.TryAdd(type, DispatchAsync))
        {
            throw new InvalidOperationException($"RPC handler already registered for {type}.");
        }

        Task DispatchAsync(RpcRequest request, string peer, PeerLink link, CancellationToken stop)
        {
            var message = request.Payload.Unpack<TRequest>();
            if (requestId(message) != request.RequestId)
            {
                throw new InvalidDataException("RPC request ID does not match its payload.");
            }

            return handler(peer, message, link, stop);
        }
    }

    public Task DispatchAsync(RpcRequest request, string peer, PeerLink link, CancellationToken stop)
    {
        ValidateEnvelope(request.RequestId, request.Payload);

        var type = Any.GetTypeName(request.Payload.TypeUrl);
        if (!handlers.TryGetValue(type, out var handler))
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, $"Unknown RPC request type {type}."));
        }

        return handler(request, peer, link, stop);
    }

    internal static void ValidateEnvelope(string requestId, Any? payload)
    {
        if (string.IsNullOrWhiteSpace(requestId) || payload is null || string.IsNullOrWhiteSpace(payload.TypeUrl))
        {
            throw new InvalidDataException("RPC requires a request ID and a typed payload.");
        }
    }
}
