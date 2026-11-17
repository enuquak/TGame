using TGame;
using TGame.Server.Engine;
using TGame.Server.Logic;
using TGame.Transport;

internal static class TestNodes
{
    public static Frame Hello(string peer) => new() { Hello = peer };

    public static async Task RegisterAsync(FrameStream stream, string[] entities, CancellationToken stop)
    {
        var request = new RegisterEntitiesRequest { SinglePointId = entities.FirstOrDefault() ?? "" };
        request.EntityIds.Add(entities);
        var frame = RpcFrames.Request(request, TestIds.Generator.GenId());
        await stream.Input.Writer.WriteAsync(frame, stop);
        var response = await stream.Output.Reader.ReadAsync(stop);
        if (response.Reply?.RequestId != frame.Request.RequestId)
        {
            throw new Exception("Entity registration acknowledgement lost its request ID.");
        }

        PeerLink.ReadReply<RegisterEntitiesReply>(response.Reply);
    }

    public static ServerGroup DefaultGroup { get; } = new(Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1));

    public static ServerNode Create(string role) =>
        Create(ServerProcess.FormatName(role, 1), DefaultGroup);

    public static ServerNode Create(string name, ServerGroup group)
    {
        var messages = new RpcDispatcher();
        var node = new ServerNode(name, group, messages, TestIds.Generator);
        ServerMessages.Register(messages, node);
        return node;
    }
}
