using TGame.Profile;
using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal static class ProfileMessages
{
    public static void Register(RpcDispatcher messages, GameServerNode node)
    {
        messages.Register<ProfileRequest>(request => request.RequestId, async (peer, request, link, stop) =>
        {
            TraceLog.Event(ServerLogEvents.ProfileRequestRx, node.Role, peer, request.RequestId,
                ProfileStore.Subject(request), link.Id);

            var reply = await node.RouteProfileAsync(peer, request, stop);
            await link.ReplyAsync(reply, stop);
        });
    }
}
