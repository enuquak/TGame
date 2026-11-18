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

    public static ServerConfiguration InitializeConfiguration(ServerGroup group)
    {
        Dictionary<ProcessType, ProcessType[]> targets;
        using (ServerConfiguration.Initialize(Path.Combine(AppContext.BaseDirectory, "config", ServerConfiguration.FileName)))
        {
            // 数量仅描述内存测试创建的节点；被测运行时只接收类型关系。
            var present = group.Processes.Select(process => process.Type).ToHashSet();
            targets = ServerConfiguration.DialTargets.ToDictionary(pair => pair.Key,
                pair => pair.Value.Where(present.Contains).ToArray());
        }

        return ServerConfiguration.Initialize(targets);
    }

    public static string[] DialTargets(string name, ServerGroup group) => group.Processes
        .Where(peer => Topology.Dials(ServerProcess.Parse(name).Type, peer.Type)).Select(peer => peer.Name).ToArray();

    public static HashSet<string> Peers(string name, ServerGroup group) => group.Processes
        .Where(peer => Topology.ArePeers(ServerProcess.Parse(name).Type, peer.Type)).Select(peer => peer.Name).ToHashSet();

    public static ServerConfiguration InitializeIsolated(ProcessType type)
    {
        return ServerConfiguration.Initialize(Enum.GetValues<ProcessType>().ToDictionary(current => current, _ => Array.Empty<ProcessType>()));
    }
}
