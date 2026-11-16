using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal static class ServerMessages
{
    public static void Register(RpcDispatcher messages, ServerNode node)
    {
        ProfileMessages.Register(messages, node);
    }
}
