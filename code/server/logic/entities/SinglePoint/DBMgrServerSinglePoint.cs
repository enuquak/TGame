using MongoDB.Driver;
using TGame.Server.Engine;
using TGame.Server.Storage;

namespace TGame.Server.Logic;

[SinglePointFor(ProcessType.DBMgr)]
internal sealed class DBMgrServerSinglePoint : ServerSinglePoint, IDisposable
{
    private const string ConfigDirectory = "config";
    private const string ConfigFileName = "dbmgr-storage.yaml";

    private MongoClient? mongo;

    protected override Task InitializeRoleAsync(CancellationToken stop)
    {
        var path = Path.Combine(AppContext.BaseDirectory, ConfigDirectory, ConfigFileName);
        var settings = MongoStorageSettings.Load(path);
        var password = Environment.GetEnvironmentVariable(ServerEnvironment.DBMgrMongoPassword);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException($"Missing credential {ServerEnvironment.DBMgrMongoPassword}.");
        }

        mongo = MongoStorage.CreateClient(settings, password);
        var database = mongo.GetDatabase(settings.DatabaseName);
        return MongoStorage.CheckConnectionAsync(database, stop);
    }

    public void Dispose() => mongo?.Dispose();
}
