using System.Threading.Channels;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server;

internal sealed class MessageDispatcher
{
    private readonly Dictionary<Frame.BodyOneofCase, Action<Frame, PeerLink>> replies = new();
    private readonly Dictionary<Frame.BodyOneofCase, Func<Frame, MessageContext, CancellationToken, Task>> requests = new();

    // Register all handlers before starting any links; dispatch only reads the tables.
    public void RegisterRequest(Frame.BodyOneofCase kind, Func<Frame, MessageContext, CancellationToken, Task> handler)
    {
        EnsureUnregistered(kind);
        requests.Add(kind, handler);
    }

    public void RegisterReply(Frame.BodyOneofCase kind, Action<Frame, PeerLink> handler)
    {
        EnsureUnregistered(kind);
        replies.Add(kind, handler);
    }

    private void EnsureUnregistered(Frame.BodyOneofCase kind)
    {
        if (requests.ContainsKey(kind) || replies.ContainsKey(kind))
        {
            throw new InvalidOperationException($"Message handler already registered: {kind}.");
        }
    }

    public void Receive(Frame frame, PeerLink link, ChannelWriter<Frame> queue)
    {
        if (replies.TryGetValue(frame.BodyCase, out var receive))
        {
            receive(frame, link);
            return;
        }

        if (!requests.ContainsKey(frame.BodyCase))
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, $"Unregistered message: {frame.BodyCase}."));
        }

        if (!queue.TryWrite(frame))
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Peer request queue is full."));
        }
    }

    public Task DispatchAsync(Frame frame, MessageContext context, CancellationToken stop) =>
        requests[frame.BodyCase](frame, context, stop);
}
