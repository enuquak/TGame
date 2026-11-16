using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TGame.Server;

internal sealed class ServerSettings
{
    public required string ProfileMongo
    {
        get; init;
    }
    public required string ProfileRedis
    {
        get; init;
    }
    public required string ZooKeeper
    {
        get; init;
    }
    public required string MongoDatabase
    {
        get; init;
    }
    public required string MongoUser
    {
        get; init;
    }
    public required string MongoPasswordVariable
    {
        get; init;
    }
    public required string RedisUser
    {
        get; init;
    }
    public required string RedisPasswordVariable
    {
        get; init;
    }
    public required string ProfileCachePrefix
    {
        get; init;
    }
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

        string[] values = [settings.ProfileMongo, settings.ProfileRedis, settings.ZooKeeper,
            settings.MongoDatabase, settings.MongoUser, settings.MongoPasswordVariable, settings.RedisUser,
            settings.RedisPasswordVariable, settings.ProfileCachePrefix, settings.DiscoveryRoot];
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
