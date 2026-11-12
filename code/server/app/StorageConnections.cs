using MongoDB.Bson;
using MongoDB.Driver;

namespace TGame.Server;

internal static class StorageConnections
{
    private const string PingCommand = "ping";
    private const string SuccessField = "ok";
    private static readonly TimeSpan ServerSelectionTimeout = TimeSpan.FromSeconds(5);

    public static MongoClient Mongo(string endpoint)
    {
        var settings = ServerSettings.Current.Value;
        return new MongoClient(new MongoClientSettings
        {
            Server = MongoServerAddress.Parse(endpoint),
            Credential = MongoCredential.CreateCredential(settings.MongoDatabase, settings.MongoUser, Password(settings.MongoPasswordVariable)),
            ServerSelectionTimeout = ServerSelectionTimeout
        });
    }

    public static string Password(string key) => Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Missing credential {key}.");

    public static async Task CheckMongoAsync(IMongoDatabase database, CancellationToken stop)
    {
        var reply = await database.RunCommandAsync<BsonDocument>(new BsonDocument(PingCommand, 1), cancellationToken: stop);
        if (reply[SuccessField].ToDouble() != 1)
        {
            throw new IOException("MongoDB health check failed.");
        }
    }
}
