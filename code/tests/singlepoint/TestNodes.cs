using TGame.Server.Engine;
using TGame.Server.Logic;

internal static class TestNodes
{
    public static GameServerNode Create(string role, bool diagnosticsEnabled = true)
    {
        var messages = new RpcDispatcher();
        var node = new GameServerNode(role, messages, diagnosticsEnabled);
        ServerMessages.Register(messages, node, diagnosticsEnabled);
        return node;
    }
}
