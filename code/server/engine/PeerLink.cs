using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server.Engine;

public sealed class PeerLink(string role, string peer, string id, IAsyncStreamWriter<Frame> output)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

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

    public async Task<TReply> CallAsync<TReply>(IMessage message, string requestId, CancellationToken stop, Action? sent = null)
        where TReply : IMessage<TReply>, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        stop.ThrowIfCancellationRequested();

        var request = new RpcRequest { RequestId = requestId, Payload = Any.Pack(message) };
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
            await WriteAsync(new Frame { Request = request }, stop);
            sent?.Invoke();

            var response = await reply.Task.WaitAsync(RequestTimeout, stop);
            if (!response.Payload.Is(new TReply().Descriptor))
            {
                throw new InvalidDataException("Unexpected RPC reply type.");
            }

            return response.Payload.Unpack<TReply>();
        }
        finally
        {
            lock (pendingChanges)
            {
                waiting.Remove(requestId);
            }
        }
    }

    public Task ReplyAsync(IMessage message, string requestId, CancellationToken stop)
    {
        var reply = new RpcReply { RequestId = requestId, Payload = Any.Pack(message) };
        return WriteAsync(new Frame { Reply = reply }, stop);
    }

    public void Receive(RpcReply reply)
    {
        RpcDispatcher.ValidateEnvelope(reply.RequestId, reply.Payload);

        TaskCompletionSource<RpcReply>? request;
        lock (pendingChanges)
        {
            waiting.TryGetValue(reply.RequestId, out request);
        }

        if (request is null)
        {
            // A timed-out call has already relinquished its slot on this shared stream.
            TraceLog.Event(ServerLogEvents.UnmatchedReply, role, peer, reply.RequestId, link: id);
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
