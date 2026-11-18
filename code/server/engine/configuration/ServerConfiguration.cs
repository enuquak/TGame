using System.Collections.ObjectModel;
using System.Text.Json;

namespace TGame.Server.Engine;

public sealed class ServerConfiguration
{
    public const string FileName = "connections.json";
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

    public static ServerConfiguration Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Connection configuration must map process types to target arrays.");
        }

        var configured = new Dictionary<ProcessType, ProcessType[]>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var type = ServerProcess.ParseType(property.Name);
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Invalid connection targets for {type}.");
            }

            var peers = new List<ProcessType>();
            foreach (var peer in property.Value.EnumerateArray())
            {
                if (peer.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"Invalid connection target for {type}.");
                }

                peers.Add(ServerProcess.ParseType(peer.GetString()!));
            }

            if (!configured.TryAdd(type, peers.ToArray()))
            {
                throw new InvalidDataException($"Repeated process type {type}.");
            }
        }

        return new ServerConfiguration(configured);
    }
}
