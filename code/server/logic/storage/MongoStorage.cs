using MongoDB.Bson;
using MongoDB.Driver;

namespace TGame.Server.Storage;

internal static class MongoStorage
{
    private const string PingCommand = "ping";
    private const string SuccessField = "ok";

    public static MongoClient CreateClient(MongoStorageSettings settings, string password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        settings.Validate();

        var options = new MongoClientSettings
        {
            Server = MongoServerAddress.Parse(settings.Endpoint),
            Credential = MongoCredential.CreateCredential(settings.AuthenticationDatabase, settings.UserName, password),
            ServerSelectionTimeout = TimeSpan.FromSeconds(settings.ServerSelectionTimeoutSeconds)
        };

        return new MongoClient(options);
    }

    public static async Task CheckConnectionAsync(IMongoDatabase database, CancellationToken stop)
    {
        var command = new BsonDocument(PingCommand, 1);
        var reply = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: stop);
        if (reply[SuccessField].ToDouble() != 1)
        {
            throw new IOException("MongoDB connection check failed.");
        }
    }
}
