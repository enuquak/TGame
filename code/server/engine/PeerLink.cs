using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server.Engine;

public sealed class PeerLink(string role, string peer, string id, IAsyncStreamWriter<Frame> output, GameLog log)
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private bool closed;
    private readonly object pendingChanges = new();
    private readonly Dictionary<string, TaskCompletionSource<RpcReply>> waiting = new();
    private readonly SemaphoreSlim writer = new(1);

    public string Role => role;
    public string Peer => peer;
    public string Id => id;

    public async Task WriteAsync(Frame frame, CancellationToken stop)
    {
        await writer.WaitAsync(stop);
        try
        {
            await output.WriteAsync(frame, stop);
        }
        finally
        {
            writer.Release();
        }
    }

    public async Task<TReply> CallAsync<TReply>(IMessage message, string requestId, CancellationToken stop,
        Action? sent = null, TimeSpan? timeout = null)
        where TReply : IMessage<TReply>, new()
    {
        var request = new RpcRequest { RequestId = requestId, Payload = Any.Pack(message) };
        var response = await CallAsync(request, stop, sent, timeout);
        return ReadReply<TReply>(response);
    }

    internal async Task<RpcReply> CallAsync(RpcRequest request, CancellationToken stop,
        Action? sent = null, TimeSpan? timeout = null)
    {
        ValidateEnvelope(request.RequestId, request.Payload);
        stop.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(timeout ?? RequestTimeout);
        var requestId = request.RequestId;
        var reply = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pendingChanges)
        {
            if (closed)
            {
                throw new IOException($"Link {role}-{peer} closed.");
            }

            if (!waiting.TryAdd(requestId, reply))
            {
                throw new InvalidDataException("Duplicate request ID.");
            }
        }

        try
        {
            await WriteAsync(new Frame { Request = request }, deadline.Token);
            sent?.Invoke();

            return await reply.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The RPC request timed out.");
        }
        finally
        {
            lock (pendingChanges)
            {
                waiting.Remove(requestId);
            }
        }
    }

    internal static void ValidateEnvelope(string requestId, Any? payload)
    {
        if (string.IsNullOrWhiteSpace(requestId) || payload is null || string.IsNullOrWhiteSpace(payload.TypeUrl))
        {
            throw new InvalidDataException("RPC requires a request ID and a typed payload.");
        }
    }

    internal static TReply ReadReply<TReply>(RpcReply response) where TReply : IMessage<TReply>, new()
    {
        if (response.Error is { } error)
        {
            throw new RpcException(new Status((StatusCode)error.StatusCode, error.Detail));
        }

        if (!response.Payload.Is(new TReply().Descriptor))
        {
            throw new InvalidDataException("Unexpected RPC reply type.");
        }

        return response.Payload.Unpack<TReply>();
    }

    public Task ReplyAsync(IMessage message, string requestId, CancellationToken stop)
    {
        var reply = new RpcReply { RequestId = requestId, Payload = Any.Pack(message) };
        return WriteAsync(new Frame { Reply = reply }, stop);
    }

    public void Receive(RpcReply reply)
    {
        if (reply.Error is { } error)
        {
            if (string.IsNullOrWhiteSpace(reply.RequestId) ||
                reply.Payload is not null ||
                !System.Enum.IsDefined((StatusCode)error.StatusCode) ||
                error.StatusCode == (int)StatusCode.OK)
            {
                throw new InvalidDataException("Invalid RPC error reply.");
            }
        }
        else
        {
            ValidateEnvelope(reply.RequestId, reply.Payload);
        }

        TaskCompletionSource<RpcReply>? request;
        lock (pendingChanges)
        {
            waiting.TryGetValue(reply.RequestId, out request);
        }

        if (request is null)
        {
            // A timed-out call has already relinquished its slot on this shared stream.
            log.Error(ServerLogEvents.UnmatchedReply, role, peer, reply.RequestId, link: id);
            return;
        }

        request.TrySetResult(reply);
    }

    public void Close()
    {
        lock (pendingChanges)
        {
            closed = true;
            foreach (var pending in waiting.Values)
            {
                pending.TrySetException(new IOException($"Link {role}-{peer} closed."));
            }
        }
    }
}
