using TGame.Transport;

namespace TGame.Server;

internal static class GMMessages
{
    public static void Register(MessageDispatcher messages)
    {
        messages.RegisterRequest(Frame.BodyOneofCase.GmPingRequest, ServeAsync);
        messages.RegisterReply(Frame.BodyOneofCase.GmPingReply, ReceiveReply);
    }

    private static void ReceiveReply(Frame frame, PeerLink link)
    {
        var reply = frame.GmPingReply;
        link.Receive(frame, reply.RequestId, reply.EntityId, ServerLogEvents.GMPongRx);
    }

    private static async Task ServeAsync(Frame frame, MessageContext context, CancellationToken stop)
    {
        var request = frame.GmPingRequest;
        TraceLog.Event(ServerLogEvents.GMPingRx, context.Node.Role, context.Peer, request.RequestId,
            request.EntityId, context.LinkId);

        var reply = await context.Node.RouteGmPingAsync(context.Peer, request, stop);
        await context.Link.ReplyAsync(reply, stop);
    }
}
