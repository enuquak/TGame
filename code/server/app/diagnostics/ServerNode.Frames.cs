using TGame.Diagnostics;
using TGame.Transport;

namespace TGame.Server;

internal sealed partial class ServerNode
{
    private static void ReceiveDiagnosticReply(Frame frame, PeerLink link)
    {
        var pong = Pong.Parser.ParseFrom(frame.DiagnosticReply);
        link.Receive(frame, pong.RequestId, pong.EntityId, ServerLogEvents.PongRx);
    }

    private async Task ServeDiagnosticFrameAsync(Frame frame, string peer, string id, PeerLink link, CancellationToken stop)
    {
        var ping = Ping.Parser.ParseFrom(frame.DiagnosticRequest);
        TraceLog.Event(ServerLogEvents.PingRx, role, peer, ping.RequestId, ping.EntityId, id);

        try
        {
            await link.ReplyAsync(await RouteAsync(peer, ping, stop), stop);
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            TraceLog.Event(ServerLogEvents.PingFailed, role, peer, ping.RequestId, ping.EntityId, id, error.Message);
            throw;
        }
    }
}
