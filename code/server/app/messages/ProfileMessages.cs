using TGame.Transport;

namespace TGame.Server;

internal static class ProfileMessages
{
    public static void Register(MessageDispatcher messages)
    {
        messages.RegisterRequest(Frame.BodyOneofCase.ProfileRequest, ServeAsync);
        messages.RegisterReply(Frame.BodyOneofCase.ProfileReply, ReceiveReply);
    }

    private static void ReceiveReply(Frame frame, PeerLink link)
    {
        var reply = frame.ProfileReply;
        link.Receive(frame, reply.RequestId, reply.EntityId, ServerLogEvents.ProfileReplyRx);
    }

    private static async Task ServeAsync(Frame frame, MessageContext context, CancellationToken stop)
    {
        var request = frame.ProfileRequest;
        TraceLog.Event(ServerLogEvents.ProfileRequestRx, context.Node.Role, context.Peer, request.RequestId,
            ProfileStore.Subject(request), context.LinkId);

        var reply = await context.Node.RouteProfileAsync(context.Peer, request, stop);
        await context.Link.ReplyAsync(reply, stop);
    }
}
