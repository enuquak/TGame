using TGame.Diagnostics;

namespace TGame.Server;

internal sealed partial class ServerNode
{
    private void RegisterDiagnosticRpc()
    {
        rpc.Register<Ping>(ping => ping.RequestId, ServePingAsync);
    }

    private async Task ServePingAsync(string peer, Ping ping, PeerLink link, CancellationToken stop)
    {
        TraceLog.Event(ServerLogEvents.PingRx, role, peer, ping.RequestId, ping.EntityId, link.Id);

        try
        {
            var pong = await RouteAsync(peer, ping, stop);
            await link.ReplyAsync(pong, stop);
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            TraceLog.Event(ServerLogEvents.PingFailed, role, peer, ping.RequestId, ping.EntityId, link.Id, error.Message);
            throw;
        }
    }
}
