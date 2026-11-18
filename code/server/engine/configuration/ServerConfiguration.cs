using System.Collections.ObjectModel;

namespace TGame.Server.Engine;

public sealed class ServerConfiguration
{
    public const string FileName = YamlConfiguration.FileName;
    public const string SectionName = "connections";
    private readonly IReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>> targets;

    internal ServerConfiguration(IReadOnlyDictionary<ProcessType, ProcessType[]> configured)
    {
        var types = Enum.GetValues<ProcessType>();
        if (configured.Count != types.Length || types.Any(type => !configured.ContainsKey(type)))
        {
            throw new InvalidDataException("Specify outgoing connections for every process type.");
        }

        var validated = new Dictionary<ProcessType, IReadOnlyList<ProcessType>>();
        foreach (var (type, peers) in configured)
        {
            if (peers.Any(peer => !Enum.IsDefined(peer) || peer == type || peer == ProcessType.Gate) ||
                peers.Distinct().Count() != peers.Length)
            {
                throw new InvalidDataException($"Invalid or repeated connection target for {type}.");
            }

            validated.Add(type, Array.AsReadOnly(peers.ToArray()));
        }

        ValidateAcyclic(validated);
        targets = new ReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>>(validated);
    }

    private static void ValidateAcyclic(IReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>> targets)
    {
        // 地址发布前要等主动依赖就绪，依赖环会使各进程一直互相等待。
        var visited = new HashSet<ProcessType>();
        var visiting = new HashSet<ProcessType>();
        foreach (var type in Enum.GetValues<ProcessType>())
        {
            Visit(type);
        }

        void Visit(ProcessType type)
        {
            if (visited.Contains(type))
            {
                return;
            }

            if (!visiting.Add(type))
            {
                throw new InvalidDataException($"Cyclic connection dependency involving {type}.");
            }

            foreach (var peer in targets[type])
            {
                Visit(peer);
            }

            visiting.Remove(type);
            visited.Add(type);
        }
    }

    public IReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>> DialTargets => targets;

    public IReadOnlyList<ProcessType> GetDialTargets(ProcessType type) => targets[type];

    public bool Dials(ProcessType source, ProcessType target) => GetDialTargets(source).Contains(target);

    public bool ArePeers(ProcessType first, ProcessType second) => Dials(first, second) || Dials(second, first);

    public ServerConfiguration(IReadOnlyDictionary<string, string[]> configured) : this(ParseTargets(configured))
    {
    }

    public static ServerConfiguration Read(YamlConfiguration configuration) =>
        new(configuration.Read<Dictionary<string, string[]>>(SectionName));

    public static ServerConfiguration Load(string path) => Read(new YamlConfiguration(path));

    private static Dictionary<ProcessType, ProcessType[]> ParseTargets(IReadOnlyDictionary<string, string[]> configured)
    {
        var result = new Dictionary<ProcessType, ProcessType[]>();
        foreach (var pair in configured)
        {
            if (pair.Value is null)
            {
                throw new InvalidDataException($"Invalid connection targets for {pair.Key}.");
            }

            var type = ServerProcess.ParseType(pair.Key);
            result.Add(type, pair.Value.Select(ServerProcess.ParseType).ToArray());
        }

        return result;
    }
}
