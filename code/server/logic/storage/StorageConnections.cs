using MongoDB.Driver;
using TGame.Server;
using TGame.Server.Storage;

namespace TGame.Server.Logic;

internal static class StorageConnections
{
    public static MongoClient Mongo(ServerConfiguration settings)
    {
        var options = settings.Mongo ?? throw new InvalidDataException("Missing MongoDB storage configuration.");
        return MongoStorage.CreateClient(options, Password(settings.MongoPasswordVariable));
    }

    public static string Password(string key) => Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Missing credential {key}.");
}
