using MongoDB.Driver;
using TGame.Server.Storage;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TGame.Server.Logic;

internal sealed class TestStorage
{
    public const string DatabaseName = "tgame_test";
    public const string UserName = "tgame_test";

    private const string ConfigDirectory = "config";
    private const string ConfigFileName = "storage.test.yaml";
    private const int ServerSelectionTimeoutSeconds = 5;

    public required string ProfileMongo
    {
        get;
        init;
    }

    public required string ProfileRedis
    {
        get;
        init;
    }

    public static readonly Lazy<TestStorage> Settings = new(() =>
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ConfigDirectory, ConfigFileName));
        return new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build().Deserialize<TestStorage>(yaml);
    });

    public static MongoClient Mongo(string endpoint)
    {
        var settings = new MongoStorageSettings
        {
            Endpoint = endpoint,
            DatabaseName = DatabaseName,
            AuthenticationDatabase = DatabaseName,
            UserName = UserName,
            ServerSelectionTimeoutSeconds = ServerSelectionTimeoutSeconds
        };

        return MongoStorage.CreateClient(settings, Password(ServerEnvironment.MongoTestPassword));
    }

    public static string Password(string key) => Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Missing credential {key}.");
}
