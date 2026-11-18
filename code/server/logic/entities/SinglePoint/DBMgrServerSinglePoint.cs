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

    protected override async Task InitializeRoleAsync(CancellationToken stop)
    {
        var path = Path.Combine(AppContext.BaseDirectory, ConfigDirectory, ConfigFileName);
        var settings = MongoStorageSettings.Load(path);
        GameLog.Info(ServerLogEvents.InitStepCompleted, nameof(ProcessType.DBMgr), entity: EntityId,
            detail: $"{nameof(MongoStorageSettings)} 配置加载完成");

        var password = Environment.GetEnvironmentVariable(ServerEnvironment.DBMgrMongoPassword);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException($"Missing credential {ServerEnvironment.DBMgrMongoPassword}.");
        }

        mongo = MongoStorage.CreateClient(settings, password);
        GameLog.Info(ServerLogEvents.InitStepCompleted, nameof(ProcessType.DBMgr), entity: EntityId,
            detail: "MongoDB 客户端创建完成");

        var database = mongo.GetDatabase(settings.DatabaseName);
        await MongoStorage.CheckConnectionAsync(database, stop);
        GameLog.Info(ServerLogEvents.InitStepCompleted, nameof(ProcessType.DBMgr), entity: EntityId,
            detail: "MongoDB 连接检查完成");
    }

    public void Dispose() => mongo?.Dispose();
}
