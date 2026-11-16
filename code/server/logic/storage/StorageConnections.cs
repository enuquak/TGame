using MongoDB.Driver;
using TGame.Server;
using TGame.Server.Storage;

namespace TGame.Server.Logic;

internal static class StorageConnections
{
    private const int ServerSelectionTimeoutSeconds = 5;

    public static MongoClient Mongo(string endpoint)
    {
        var settings = ServerSettings.Current.Value;
        var options = new MongoStorageSettings
        {
            Endpoint = endpoint,
            DatabaseName = settings.MongoDatabase,
            AuthenticationDatabase = settings.MongoDatabase,
            UserName = settings.MongoUser,
            ServerSelectionTimeoutSeconds = ServerSelectionTimeoutSeconds
        };

        return MongoStorage.CreateClient(options, Password(settings.MongoPasswordVariable));
    }

    public static string Password(string key) => Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Missing credential {key}.");
}
