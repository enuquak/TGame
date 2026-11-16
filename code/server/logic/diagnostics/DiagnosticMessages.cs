using TGame.Diagnostics;
using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal static class DiagnosticMessages
{
    public static void Register(RpcDispatcher messages, GameServerNode node)
    {
        messages.Register<Ping>(ping => ping.RequestId, async (peer, ping, link, stop) =>
        {
            TraceLog.Event(ServerLogEvents.PingRx, node.Role, peer, ping.RequestId, ping.EntityId, link.Id);

            try
            {
                var pong = await node.RouteAsync(peer, ping, stop);
                await link.ReplyAsync(pong, stop);
            }
            catch (Exception error) when (!stop.IsCancellationRequested)
            {
                TraceLog.Event(ServerLogEvents.PingFailed, node.Role, peer, ping.RequestId,
                    ping.EntityId, link.Id, error.Message);
                throw;
            }
        });
    }
}
