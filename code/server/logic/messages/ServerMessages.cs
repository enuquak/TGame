using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal static class ServerMessages
{
    public static void Register(RpcDispatcher messages, GameServerNode node, bool diagnosticsEnabled = false)
    {
        ProfileMessages.Register(messages, node);

        if (diagnosticsEnabled)
        {
            DiagnosticMessages.Register(messages, node);
        }
    }
}
