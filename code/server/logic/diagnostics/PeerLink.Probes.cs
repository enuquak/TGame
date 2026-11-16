using TGame.Server.Engine;
using TGame.Diagnostics;

namespace TGame.Server.Logic;

internal static partial class PeerLinkMessages
{
    public static async Task<Pong> PingAsync(this PeerLink link, Ping ping, CancellationToken stop)
    {
        var pong = await link.CallAsync<Pong>(ping, ping.RequestId, stop,
            () => TraceLog.Event(ServerLogEvents.PingTx, link.Role, link.Peer, ping.RequestId, ping.EntityId, link.Id));
        TraceLog.Event(ServerLogEvents.PongRx, link.Role, link.Peer, pong.RequestId, pong.EntityId, link.Id);

        if (pong.RequestId != ping.RequestId || pong.EntityId != ping.EntityId)
        {
            throw new InvalidDataException("Pong request or entity mismatch.");
        }

        return pong;
    }

    public static async Task ReplyAsync(this PeerLink link, Pong pong, CancellationToken stop)
    {
        await link.ReplyAsync(pong, pong.RequestId, stop);
        TraceLog.Event(ServerLogEvents.PongTx, link.Role, link.Peer, pong.RequestId, pong.EntityId, link.Id);
    }
}
