using TGame.Diagnostics;
using TGame.Transport;

namespace TGame.Server;

internal sealed partial class PeerLink
{
    public async Task<Pong> PingAsync(Ping ping, CancellationToken stop)
    {
        var frame = await CallAsync(new Frame { Ping = ping }, ping.RequestId, ping.EntityId, ServerLogEvents.PingTx, stop);
        if (frame.Pong?.EntityId != ping.EntityId)
        {
            throw new InvalidDataException("Pong entity mismatch.");
        }

        return frame.Pong;
    }

    public async Task ReplyAsync(Pong pong, CancellationToken stop)
    {
        await WriteAsync(new Frame { Pong = pong }, stop);
        TraceLog.Event(ServerLogEvents.PongTx, role, peer, pong.RequestId, pong.EntityId, id);
    }
}
