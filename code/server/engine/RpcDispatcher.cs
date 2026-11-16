using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server.Engine;

public sealed class RpcDispatcher
{
    private readonly Dictionary<string, Func<RpcRequest, string, PeerLink, CancellationToken, Task>> handlers = new();
    private readonly Dictionary<string, Func<EntityRpcContext, Any, CancellationToken, Task<IMessage>>> entityHandlers = new();
    private readonly Dictionary<string, Func<EntityRpcContext, Any, IMessage?>> entityValidators = new();

    public void RegisterEntity<TRequest, TReply>(Func<EntityRpcContext, TRequest, CancellationToken, Task<TReply>> handler,
        Func<EntityRpcContext, TRequest, TReply?>? validate = null)
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

        if (validate is not null)
        {
            entityValidators.Add(type, (context, payload) => validate(context, payload.Unpack<TRequest>()));
        }
    }

    // A business rejection is returned before forwarding; null allows normal routing.
    internal IMessage? ValidateEntity(RpcRequest request, string? peer)
    {
        var type = Any.GetTypeName(request.Payload.TypeUrl);
        if (!entityValidators.TryGetValue(type, out var validate))
        {
            return null;
        }

        var context = new EntityRpcContext(request.RequestId, request.SourceProcess, request.EntityId, peer);
        return validate(context, request.Payload);
    }

    internal Task<IMessage> DispatchEntityAsync(RpcRequest request, CancellationToken stop, string? peer = null)
    {
        var type = Any.GetTypeName(request.Payload.TypeUrl);
        if (!entityHandlers.TryGetValue(type, out var handler))
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, $"Unknown entity RPC request type {type}."));
        }

        var context = new EntityRpcContext(request.RequestId, request.SourceProcess, request.EntityId, peer);
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
