using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TGame.Server;

internal sealed class ServerSettings
{
    public ServerLogLevel MinimumLogLevel { get; init; } = ServerLogLevel.Info;

    public string ProfileMongo
    {
        get; init;
    } = string.Empty;
    public string ProfileRedis
    {
        get; init;
    } = string.Empty;
    public required string ZooKeeper
    {
        get; init;
    }
    public string MongoDatabase
    {
        get; init;
    } = string.Empty;
    public string MongoUser
    {
        get; init;
    } = string.Empty;
    public string MongoPasswordVariable
    {
        get; init;
    } = string.Empty;
    public string RedisUser
    {
        get; init;
    } = string.Empty;
    public string RedisPasswordVariable
    {
        get; init;
    } = string.Empty;
    public string ProfileCachePrefix
    {
        get; init;
    } = string.Empty;
    public required string DiscoveryRoot
    {
        get; init;
    }

    public static readonly Lazy<ServerSettings> Current = new(() =>
    {
        var path = Environment.GetEnvironmentVariable(ServerEnvironment.Config);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Select a server configuration with {ServerEnvironment.Config}.");
        }

        return Load(path);
    });

    public void ValidateProfileStorage()
    {
        string[] values = [ProfileMongo, ProfileRedis, MongoDatabase, MongoUser,
            MongoPasswordVariable, RedisUser, RedisPasswordVariable, ProfileCachePrefix];
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("Incomplete Profile storage configuration.");
        }
    }

    public static ServerSettings Load(string path)
    {
        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var settings = deserializer.Deserialize<ServerSettings>(yaml);
        if (settings is null)
        {
            throw new InvalidDataException($"Empty server configuration: {path}.");
        }

        if (!Enum.IsDefined(settings.MinimumLogLevel))
        {
            throw new InvalidDataException("Minimum log level must be DEBUG, INFO or ERROR.");
        }

        string[] values = [settings.ZooKeeper, settings.DiscoveryRoot];
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"Incomplete server configuration: {path}.");
        }

        if (!settings.DiscoveryRoot.StartsWith('/') || settings.DiscoveryRoot.EndsWith('/') || settings.DiscoveryRoot.Contains("//"))
        {
            throw new InvalidDataException("Discovery root must be an absolute ZooKeeper path without a trailing slash.");
        }

        return settings;
    }
}
