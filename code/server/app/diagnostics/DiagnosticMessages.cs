using TGame.Diagnostics;
using TGame.Transport;

namespace TGame.Server;

internal static class DiagnosticMessages
{
    public static void Register(MessageDispatcher messages)
    {
        messages.RegisterRequest(Frame.BodyOneofCase.DiagnosticRequest, ServeAsync);
        messages.RegisterReply(Frame.BodyOneofCase.DiagnosticReply, ReceiveReply);
    }

    private static void ReceiveReply(Frame frame, PeerLink link)
    {
        var pong = Pong.Parser.ParseFrom(frame.DiagnosticReply);
        link.Receive(frame, pong.RequestId, pong.EntityId, ServerLogEvents.PongRx);
    }

    private static async Task ServeAsync(Frame frame, MessageContext context, CancellationToken stop)
    {
        var ping = Ping.Parser.ParseFrom(frame.DiagnosticRequest);
        TraceLog.Event(ServerLogEvents.PingRx, context.Node.Role, context.Peer, ping.RequestId,
            ping.EntityId, context.LinkId);

        try
        {
            var pong = await context.Node.RouteAsync(context.Peer, ping, stop);
            await context.Link.ReplyAsync(pong, stop);
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            TraceLog.Event(ServerLogEvents.PingFailed, context.Node.Role, context.Peer, ping.RequestId,
                ping.EntityId, context.LinkId, error.Message);
            throw;
        }
    }
}
