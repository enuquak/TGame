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
        var frame = RpcFrames.Request(request, IdGenerator.GenId());
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
        new(ServerProcess.FormatName(role, 1));

    public static ServerConfiguration InitializeIsolated(ProcessType type)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(current => current, current => current == type ? 1 : 0);
        return ServerConfiguration.Initialize(new ServerGroup(counts));
    }
}
