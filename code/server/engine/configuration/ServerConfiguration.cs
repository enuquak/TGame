using System.Collections.ObjectModel;
using System.Text.Json;

namespace TGame.Server.Engine;

public sealed class ServerConfiguration : IDisposable
{
    public const string FileName = "connections.json";
    private static ServerConfiguration? instance;
    private readonly IReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>> targets;

    private ServerConfiguration(IReadOnlyDictionary<ProcessType, ProcessType[]> configured)
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

        // 地址发布前要等主动依赖就绪，依赖环会使各进程一直互相等待。
        var visited = new HashSet<ProcessType>();
        var visiting = new HashSet<ProcessType>();
        foreach (var type in types)
        {
            Visit(type);
        }

        targets = new ReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>>(validated);

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

            foreach (var peer in validated[type])
            {
                Visit(peer);
            }

            visiting.Remove(type);
            visited.Add(type);
        }
    }

    public static IReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>> DialTargets =>
        Volatile.Read(ref instance)?.targets
        ?? throw new InvalidOperationException("Initialize the server configuration before use.");

    public static ServerConfiguration Initialize(string path)
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

        return Initialize(configured);
    }

    internal static ServerConfiguration Initialize(IReadOnlyDictionary<ProcessType, ProcessType[]> targets)
    {
        var initialized = new ServerConfiguration(targets);
        if (Interlocked.CompareExchange(ref instance, initialized, null) is not null)
        {
            throw new InvalidOperationException("The server configuration is already initialized.");
        }

        return initialized;
    }

    public void Dispose()
    {
        Interlocked.CompareExchange(ref instance, null, this);
    }
}
