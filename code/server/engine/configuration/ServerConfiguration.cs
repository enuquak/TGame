using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TGame.Server.Engine;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TGame;

/// <summary>统一加载、校验并持有服务器配置及连接规则，由宿主注册为 IoC 单例。</summary>
public sealed class ServerConfiguration
{
    public const string FileName = "server.yaml";
    public const string IncludeKey = "include";
    public const string RunDirectoryKey = "runDirectory";
    public const string SectionName = "connections";
    private const int MaximumNodeDepth = 64;
    private readonly YamlMappingNode root;
    private readonly IReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>> targets;

    public string Environment { get; }
    public string RunDirectory { get; }
    public ServerLogLevel LoggingMinimumLevel { get; }
    public MongoStorageSettings? Mongo { get; }
    public RedisStorageSettings? Redis { get; }
    public ZooKeeperOptions ZooKeeperOptions { get; }
    public IdGeneratorOptions Identifiers { get; }
    public ServerGroup Group { get; }

    public string ZooKeeper => ZooKeeperOptions.Endpoint;
    public string DiscoveryRoot => ZooKeeperOptions.DiscoveryRoot;
    public string ProfileMongo => Mongo?.Endpoint ?? "";
    public string ProfileRedis => Redis?.Endpoint ?? "";
    public string MongoDatabase => Mongo?.DatabaseName ?? "";
    public string MongoUser => Mongo?.UserName ?? "";
    public string MongoPasswordVariable => Mongo?.PasswordVariables.Profile ?? "";
    public string RedisUser => Redis?.UserName ?? "";
    public string RedisPasswordVariable => Redis?.PasswordVariable ?? "";
    public string ProfileCachePrefix => Redis?.ProfileCachePrefix ?? "";

    public static string ConfigurationPath => Path.Combine("code", "server", "config", FileName);
    public string SourcePath { get; }

    public ServerConfiguration(string path)
    {
        SourcePath = Path.GetFullPath(path);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        root = Load(SourcePath, new HashSet<string>(comparer));
        var document = Read<ConfigurationDocument>();
        Environment = document.Environment;
        RunDirectory = document.RunDirectory;
        LoggingMinimumLevel = document.LoggingMinimumLevel;
        Mongo = document.Mongo;
        Redis = document.Redis;
        ZooKeeperOptions = document.ZooKeeperOptions;

        if (string.IsNullOrWhiteSpace(Environment) || !Enum.IsDefined(LoggingMinimumLevel))
        {
            throw new InvalidDataException("Specify an environment and a loggingMinimumLevel of DEBUG, INFO or ERROR.");
        }

        ValidateRunDirectory(RunDirectory);
        if (ZooKeeperOptions is null)
        {
            throw new InvalidDataException("Missing zooKeeperOptions configuration.");
        }

        Identifiers = new IdGeneratorOptions { WorkerRootPath = ZooKeeperOptions.WorkerRootPath };
        try
        {
            ZooKeeperOptions.Validate();
            Identifiers.Validate();
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Invalid zooKeeperOptions configuration.", error);
        }

        Group = new ServerGroup(document.Processes);
        targets = ValidateTargets(ParseTargets(document.Connections));
    }

    public T Read<T>(string? section = null)
    {
        YamlNode node = root;
        if (section is not null && !root.Children.TryGetValue(new YamlScalarNode(section), out node!))
        {
            throw new InvalidDataException($"Missing configuration section '{section}' in {SourcePath}.");
        }

        var builder = new DeserializerBuilder();
        builder.WithNamingConvention(CamelCaseNamingConvention.Instance);
        builder.WithDuplicateKeyChecking();
        try
        {
            var value = builder.Build().Deserialize<T>(Serialize(node));
            if (value is null)
            {
                throw new InvalidDataException($"Empty configuration section '{section}' in {SourcePath}.");
            }

            return value;
        }
        catch (YamlException error)
        {
            throw new InvalidDataException($"Invalid configuration section '{section ?? "root"}' in {SourcePath}.", error);
        }
    }

    public string ResolveRunDirectory(string workingDirectory)
    {
        return Path.GetFullPath(RunDirectory, Path.GetFullPath(workingDirectory));
    }

    internal static void ValidateRunDirectory(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("runDirectory must be a nonempty relative path from the working directory.");
        }
    }

    private static YamlMappingNode Load(string path, HashSet<string> loading)
    {
        if (!loading.Add(path))
        {
            throw new InvalidDataException($"Circular configuration include: {path}.");
        }

        try
        {
            using var input = File.OpenText(path);
            var stream = new YamlStream();
            stream.Load(input);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode document)
            {
                throw new InvalidDataException($"Configuration must contain one YAML mapping document: {path}.");
            }

            // 展开本文件的别名，避免合并后不同文件使用同名锚点时互相影响。
            var expanded = (YamlMappingNode)CopyNode(document, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance), 0);
            var merged = new YamlMappingNode();
            var includeKey = new YamlScalarNode(IncludeKey);
            if (expanded.Children.TryGetValue(includeKey, out var includes))
            {
                if (includes is not YamlSequenceNode sequence)
                {
                    throw new InvalidDataException($"include must be a list of file names: {path}.");
                }

                var names = new HashSet<string>(loading.Comparer);
                foreach (var item in sequence.Children)
                {
                    if (item is not YamlScalarNode scalar || !IsIncludeName(scalar.Value) || !names.Add(scalar.Value!))
                    {
                        throw new InvalidDataException($"Invalid or repeated include name in {path}.");
                    }

                    var included = Path.Combine(Path.GetDirectoryName(path)!, scalar.Value + ".yaml");
                    Merge(merged, Load(included, loading), path);
                }

                expanded.Children.Remove(includeKey);
            }

            Merge(merged, expanded, path);
            return merged;
        }
        catch (YamlException error)
        {
            throw new InvalidDataException($"Invalid YAML configuration: {path}.", error);
        }
        finally
        {
            loading.Remove(path);
        }
    }

    private static bool IsIncludeName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            name is not "." and not ".." &&
            !Path.HasExtension(name) &&
            !name.Contains('/') &&
            !name.Contains('\\') &&
            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static void Merge(YamlMappingNode target, YamlMappingNode source, string path)
    {
        foreach (var pair in source.Children)
        {
            if (pair.Key is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
            {
                throw new InvalidDataException($"Configuration keys must be nonempty strings: {path}.");
            }

            if (target.Children.ContainsKey(key))
            {
                throw new InvalidDataException($"Duplicate top-level configuration key '{key.Value}' in {path}.");
            }

            target.Add(key, pair.Value);
        }
    }

    private static YamlNode CopyNode(YamlNode node, HashSet<YamlNode> visiting, int depth)
    {
        if (depth > MaximumNodeDepth || !visiting.Add(node))
        {
            throw new InvalidDataException($"Configuration aliases must be acyclic and nesting must be at most {MaximumNodeDepth} levels.");
        }

        try
        {
            if (node is YamlScalarNode scalar)
            {
                return new YamlScalarNode(scalar.Value) { Style = scalar.Style, Tag = scalar.Tag };
            }

            if (node is YamlSequenceNode sequence)
            {
                var result = new YamlSequenceNode();
                foreach (var child in sequence.Children)
                {
                    result.Add(CopyNode(child, visiting, depth + 1));
                }

                return result;
            }

            if (node is YamlMappingNode mapping)
            {
                var result = new YamlMappingNode();
                foreach (var pair in mapping.Children)
                {
                    result.Add(CopyNode(pair.Key, visiting, depth + 1), CopyNode(pair.Value, visiting, depth + 1));
                }

                return result;
            }

            throw new InvalidDataException("Unsupported YAML configuration node.");
        }
        finally
        {
            visiting.Remove(node);
        }
    }

    private static string Serialize(YamlNode node)
    {
        using var output = new StringWriter();
        var stream = new YamlStream(new YamlDocument(node));
        stream.Save(output, assignAnchors: false);
        return output.ToString();
    }

    public static string SelectedPath()
    {
        var path = System.Environment.GetEnvironmentVariable(EngineEnvironment.Config);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Select a server configuration with {EngineEnvironment.Config}.");
        }

        return path;
    }

    public static ServerConfiguration LoadSelected() => Load(SelectedPath());

    public static ServerConfiguration Load(string path) => new(path);

    public void ValidateProfileStorage()
    {
        if (Mongo is null || Redis is null || string.IsNullOrWhiteSpace(Mongo.PasswordVariables.Profile))
        {
            throw new InvalidDataException("Incomplete Profile storage configuration.");
        }

        Mongo.Validate();
        Redis.Validate();
    }

    private static IReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>> ValidateTargets(
        IReadOnlyDictionary<ProcessType, ProcessType[]> configured)
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
        return new ReadOnlyDictionary<ProcessType, IReadOnlyList<ProcessType>>(validated);
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

    private static Dictionary<ProcessType, ProcessType[]> ParseTargets(IReadOnlyDictionary<string, string[]> configured)
    {
        if (configured is null)
        {
            throw new InvalidDataException("Missing connections configuration.");
        }

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

    // 只用于一次性严格绑定 YAML，加载完成后仅保留经过校验的配置值与原始映射。
    private sealed class ConfigurationDocument
    {
        public ConfigurationDocument()
        {
        }

        public string Environment { get; init; } = "";
        public string RunDirectory { get; init; } = "";

        public ServerLogLevel LoggingMinimumLevel { get; init; } = ServerLogLevel.Info;

        public Dictionary<string, int> Processes { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, string[]> Connections { get; init; } = new(StringComparer.Ordinal);
        public MongoStorageSettings? Mongo { get; init; }
        public RedisStorageSettings? Redis { get; init; }

        public ZooKeeperOptions ZooKeeperOptions { get; init; } = new()
        {
            SessionTimeoutMilliseconds = 0,
            ConnectTimeoutMilliseconds = 0
        };

    }
}
