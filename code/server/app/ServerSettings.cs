using TGame.Server.Engine;
using TGame.Server.Storage;
using YamlDotNet.Serialization;

namespace TGame.Server;

internal sealed class ServerSettings
{
    public string Environment { get; init; } = "";
    public string RunDirectory { get; init; } = "";

    [YamlMember(Alias = "loggingMinimumLevel")]
    public ServerLogLevel MinimumLogLevel { get; init; } = ServerLogLevel.Info;

    public Dictionary<string, int> Processes { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> Connections { get; init; } = new(StringComparer.Ordinal);
    public MongoStorageSettings? Mongo { get; init; }
    public RedisStorageSettings? Redis { get; init; }

    [YamlMember(Alias = "zooKeeper")]
    public ZooKeeperOptions ZooKeeperSettings { get; init; } = new()
    {
        SessionTimeoutMilliseconds = 0,
        ConnectTimeoutMilliseconds = 0
    };

    // 存储逻辑读取同一份嵌套配置，不再维护旧扁平字段的副本。
    [YamlIgnore] public string ZooKeeper => ZooKeeperSettings.Endpoint;
    [YamlIgnore] public string DiscoveryRoot => ZooKeeperSettings.DiscoveryRoot;
    [YamlIgnore] public string ProfileMongo => Mongo?.Endpoint ?? "";
    [YamlIgnore] public string ProfileRedis => Redis?.Endpoint ?? "";
    [YamlIgnore] public string MongoDatabase => Mongo?.DatabaseName ?? "";
    [YamlIgnore] public string MongoUser => Mongo?.UserName ?? "";
    [YamlIgnore] public string MongoPasswordVariable => Mongo?.PasswordVariables.Profile ?? "";
    [YamlIgnore] public string RedisUser => Redis?.UserName ?? "";
    [YamlIgnore] public string RedisPasswordVariable => Redis?.PasswordVariable ?? "";
    [YamlIgnore] public string ProfileCachePrefix => Redis?.ProfileCachePrefix ?? "";

    public static string SelectedPath()
    {
        var path = System.Environment.GetEnvironmentVariable(ServerEnvironment.Config);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Select a server configuration with {ServerEnvironment.Config}.");
        }

        return path;
    }

    public static ServerSettings LoadSelected() => Load(SelectedPath());

    public static ServerSettings Load(string path) => From(new YamlConfiguration(path));

    public static ServerSettings From(YamlConfiguration configuration)
    {
        var settings = configuration.Read<ServerSettings>();
        if (string.IsNullOrWhiteSpace(settings.Environment) || !Enum.IsDefined(settings.MinimumLogLevel))
        {
            throw new InvalidDataException("Specify an environment and a loggingMinimumLevel of DEBUG, INFO or ERROR.");
        }

        _ = configuration.ResolveRunDirectory(Directory.GetCurrentDirectory());
        if (settings.ZooKeeperSettings is null)
        {
            throw new InvalidDataException("Missing zooKeeper configuration.");
        }

        try
        {
            settings.ZooKeeperSettings.Validate();
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Invalid zooKeeper configuration.", error);
        }
        return settings;
    }

    public void ValidateProfileStorage()
    {
        if (Mongo is null || Redis is null || string.IsNullOrWhiteSpace(Mongo.PasswordVariables.Profile))
        {
            throw new InvalidDataException("Incomplete Profile storage configuration.");
        }

        Mongo.Validate();
        Redis.Validate();
    }
}
