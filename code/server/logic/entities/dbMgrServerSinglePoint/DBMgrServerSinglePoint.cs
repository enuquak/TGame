using MongoDB.Driver;
using TGame.PingPong;

namespace TGame.Server.Logic;

[SinglePointFor(ProcessType.DBMgr)]
internal sealed class DBMgrServerSinglePoint : ServerSinglePoint, IDisposable
{
    private MongoClient? mongo;

    protected override Task InitializeRoleAsync(CancellationToken stop)
    {
        mongo = TestStorage.Mongo(TestStorage.Settings.Value.DbMgrMongo);
        return PingAsync(stop);
    }

    public Task PingAsync(CancellationToken stop) => TestStorage.PingAsync(mongo!.GetDatabase(TestStorage.DatabaseName), stop);

    public void Dispose() => mongo?.Dispose();
}
