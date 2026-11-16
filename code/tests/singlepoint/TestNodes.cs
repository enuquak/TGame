using TGame;
using TGame.Server.Engine;
using TGame.Server.Logic;

internal static class TestNodes
{
    public static TGame.Transport.Frame Hello(string peer, string? entityId = null) => new()
    {
        Hello = peer,
        SinglePointId = peer.StartsWith(nameof(ProcessType.Gate), StringComparison.Ordinal)
            ? ""
            : entityId ?? TestIds.Generator.GenId().ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    public static ServerGroup DefaultGroup { get; } = new(Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1));

    public static GameServerNode Create(string role) =>
        Create(ServerProcess.FormatName(role, 1), DefaultGroup);

    public static GameServerNode Create(string name, ServerGroup group)
    {
        var messages = new RpcDispatcher();
        var node = new GameServerNode(name, group, messages, TestIds.Generator);
        ServerMessages.Register(messages, node);
        return node;
    }
}
