using Google.Protobuf;
using TGame.Diagnostics;
using TGame.Transport;

namespace TGame.Server;

internal sealed partial class PeerLink
{
    public async Task<Pong> PingAsync(Ping ping, CancellationToken stop)
    {
        var frame = await CallAsync(new Frame { DiagnosticRequest = ping.ToByteString() }, ping.RequestId, ping.EntityId, ServerLogEvents.PingTx, stop);
        var pong = Pong.Parser.ParseFrom(frame.DiagnosticReply);
        if (pong.EntityId != ping.EntityId)
        {
            throw new InvalidDataException("Pong entity mismatch.");
        }

        return pong;
    }

    public async Task ReplyAsync(Pong pong, CancellationToken stop)
    {
        await WriteAsync(new Frame { DiagnosticReply = pong.ToByteString() }, stop);
        TraceLog.Event(ServerLogEvents.PongTx, role, peer, pong.RequestId, pong.EntityId, id);
    }
}
