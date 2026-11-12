using MongoDB.Driver;
using TGame.Server;

namespace TGame.Server.Logic;

[SinglePointFor(ProcessType.DBMgr)]
internal sealed class DBMgrServerSinglePoint : ServerSinglePoint, IDisposable
{
    private MongoClient? mongo;

    protected override Task InitializeRoleAsync(CancellationToken stop)
    {
        mongo = StorageConnections.Mongo(ServerSettings.Current.Value.DbMgrMongo);
        return PingAsync(stop);
    }

    public Task PingAsync(CancellationToken stop) => StorageConnections.CheckMongoAsync(mongo!.GetDatabase(ServerSettings.Current.Value.MongoDatabase), stop);

    public void Dispose() => mongo?.Dispose();
}
