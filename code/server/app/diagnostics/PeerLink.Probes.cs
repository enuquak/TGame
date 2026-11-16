using TGame.Diagnostics;

namespace TGame.Server;

internal sealed partial class PeerLink
{
    public async Task<Pong> PingAsync(Ping ping, CancellationToken stop)
    {
        var pong = await CallAsync<Pong>(ping, ping.RequestId, stop,
            () => TraceLog.Event(ServerLogEvents.PingTx, role, peer, ping.RequestId, ping.EntityId, id));
        TraceLog.Event(ServerLogEvents.PongRx, role, peer, pong.RequestId, pong.EntityId, id);

        if (pong.RequestId != ping.RequestId || pong.EntityId != ping.EntityId)
        {
            throw new InvalidDataException("Pong request or entity mismatch.");
        }

        return pong;
    }

    public async Task ReplyAsync(Pong pong, CancellationToken stop)
    {
        await ReplyAsync(pong, pong.RequestId, stop);
        TraceLog.Event(ServerLogEvents.PongTx, role, peer, pong.RequestId, pong.EntityId, id);
    }
}
