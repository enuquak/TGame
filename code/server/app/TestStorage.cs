using MongoDB.Bson;
using MongoDB.Driver;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TGame.PingPong;

internal sealed class TestStorage
{
    public const string DatabaseName = "tgame_test";
    public const string UserName = "tgame_test";

    private const string ConfigDirectory = "config";
    private const string ConfigFileName = "storage.test.yaml";
    private const string PingCommand = "ping";
    private const string SuccessField = "ok";
    private static readonly TimeSpan ServerSelectionTimeout = TimeSpan.FromSeconds(5);

    public required string DbMgrMongo
    {
        get;
        init;
    }

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

    public required string ZooKeeper { get; init; }

    public static readonly Lazy<TestStorage> Settings = new(() =>
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ConfigDirectory, ConfigFileName));
        return new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build().Deserialize<TestStorage>(yaml);
    });

    public static MongoClient Mongo(string endpoint) => new(new MongoClientSettings
    {
        Server = MongoServerAddress.Parse(endpoint),
        Credential = MongoCredential.CreateCredential(DatabaseName, UserName, Password(ServerEnvironment.MongoTestPassword)),
        ServerSelectionTimeout = ServerSelectionTimeout
    });

    public static string Password(string key) => Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Missing credential {key}.");

    public static async Task PingAsync(IMongoDatabase database, CancellationToken stop)
    {
        var reply = await database.RunCommandAsync<BsonDocument>(new BsonDocument(PingCommand, 1), cancellationToken: stop);
        if (reply[SuccessField].ToDouble() != 1)
        {
            throw new IOException("MongoDB ping failed.");
        }
    }
}
