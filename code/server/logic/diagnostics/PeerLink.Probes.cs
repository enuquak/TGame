using TGame.Diagnostics;
using TGame.Server.Engine;
using TGame.Transport;

namespace TGame.Server.Logic;

internal static partial class PeerLinkMessages
{
    public static async Task<Pong> PingAsync(this PeerLink link, Ping ping, CancellationToken stop)
    {
        var frame = await link.CallAsync(new Frame { Ping = ping }, ping.RequestId, ping.EntityId, ServerLogEvents.PingTx, stop);
        if (frame.Pong?.EntityId != ping.EntityId)
        {
            throw new InvalidDataException("Pong entity mismatch.");
        }

        return frame.Pong;
    }

    public static async Task ReplyAsync(this PeerLink link, Pong pong, CancellationToken stop)
    {
        await link.WriteAsync(new Frame { Pong = pong }, stop);
        TraceLog.Event(ServerLogEvents.PongTx, link.Role, link.Peer, pong.RequestId, pong.EntityId, link.Id);
    }
}
