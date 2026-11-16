using TGame;
using TGame.Server.Engine;
using TGame.Server.Logic;

internal static class TestNodes
{
    public static ServerGroup DefaultGroup { get; } = new(Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1));

    public static GameServerNode Create(string role, bool diagnosticsEnabled = true) =>
        Create(ServerProcess.FormatName(role, 1), DefaultGroup, diagnosticsEnabled);

    public static GameServerNode Create(string name, ServerGroup group, bool diagnosticsEnabled = true)
    {
        var messages = new RpcDispatcher();
        var node = new GameServerNode(name, group, messages, TestIds.Generator, diagnosticsEnabled);
        ServerMessages.Register(messages, node, diagnosticsEnabled);
        return node;
    }
}
