using MongoDB.Bson;
using MongoDB.Driver;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TGame.PingPong;

internal sealed class TestStorage
{
    public required string DbMgrMongo { get; init; }
    public required string ProfileMongo { get; init; }
    public required string ProfileRedis { get; init; }

    public static readonly Lazy<TestStorage> Settings = new(() =>
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "storage.test.yaml"));
        return new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build().Deserialize<TestStorage>(yaml);
    });

    public static MongoClient Mongo(string endpoint) => new(new MongoClientSettings
    {
        Server = MongoServerAddress.Parse(endpoint),
        Credential = MongoCredential.CreateCredential("tgame_test", "tgame_test", Password("TGAME_MONGO_TEST_PASSWORD")),
        ServerSelectionTimeout = TimeSpan.FromSeconds(5)
    });

    public static string Password(string key) => Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Missing credential {key}.");

    public static async Task PingAsync(IMongoDatabase database, CancellationToken stop)
    {
        var reply = await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: stop);
        if (reply["ok"].ToDouble() != 1) throw new IOException("MongoDB ping failed.");
    }
}
