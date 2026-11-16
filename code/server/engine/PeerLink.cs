using System.Collections.Concurrent;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server.Engine;

public sealed class PeerLink(string role, string peer, string id, IAsyncStreamWriter<Frame> output)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Frame>> waiting = new();
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

    public async Task<Frame> CallAsync(Frame frame, string requestId, string entity, string ev, CancellationToken stop)
    {
        var reply = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!waiting.TryAdd(requestId, reply))
        {
            throw new InvalidDataException("Duplicate request ID.");
        }

        try
        {
            await WriteAsync(frame, stop);
            TraceLog.Event(ev, role, peer, requestId, entity, id);
            return await reply.Task.WaitAsync(RequestTimeout, stop);
        }
        finally
        {
            waiting.TryRemove(requestId, out _);
        }
    }

    public void Receive(Frame frame, string requestId, string entity, string ev)
    {
        if (!waiting.TryGetValue(requestId, out var request))
        {
            // A timed-out call has already relinquished its slot on this shared stream.
            TraceLog.Event(ServerLogEvents.UnmatchedReply, role, peer, requestId, entity, id);
            return;
        }

        TraceLog.Event(ev, role, peer, requestId, entity, id);
        request.TrySetResult(frame);
    }

    public void Close()
    {
        foreach (var pending in waiting.Values)
        {
            pending.TrySetException(new IOException($"Link {role}-{peer} closed."));
        }
    }
}
