using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal static class TestNodes
{
    public static Frame Hello(string peer) => new() { Hello = peer };

    public static ServerGroup DefaultGroup { get; } = new(Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1));

    public static ServerConfiguration LoadConfiguration(ServerGroup group)
    {
        var configuration = ServerConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "config", ServerConfiguration.FileName));
        // 数量仅描述内存测试创建的节点；被测运行时只接收类型关系。
        var present = group.Processes.Select(process => process.Type).ToHashSet();
        var targets = configuration.DialTargets.ToDictionary(pair => pair.Key,
            pair => pair.Value.Where(present.Contains).ToArray());
        return new ServerConfiguration(targets);
    }

}
