using TGame.Management;
using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal static class GMMessages
{
    public static void Register(RpcDispatcher messages, GameServerNode node)
    {
        messages.Register<GmPingRequest>(request => request.RequestId, async (peer, request, link, stop) =>
        {
            TraceLog.Event(ServerLogEvents.GMPingRx, node.Role, peer, request.RequestId, request.EntityId, link.Id);

            var reply = await node.RouteGmPingAsync(peer, request, stop);
            await link.ReplyAsync(reply, stop);
        });
    }
}
