using TGame.Server.Engine;
using TGame;
using TGame.Server;

internal static class SettingsChecks
{
    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tgame-settings-{Guid.NewGuid().ToString("N")}.yaml");
        try
        {
            var core = """
                environment: staging
                runDirectory: logs/server-runs
                zooKeeper:
                  endpoint: zk-a:2181
                  discoveryRoot: /game/staging
                  workerRootPath: /game/workers
                  sessionTimeoutMilliseconds: 10000
                  connectTimeoutMilliseconds: 10000
                """;
            core += "\nprocesses:\n" + string.Join("\n", Enum.GetValues<ProcessType>().Select(type => $"  {type}: 1"));
            core += "\nconnections:\n" + string.Join("\n", Enum.GetValues<ProcessType>().Select(type => $"  {type}: []"));
            const string storage = """
                mongo:
                  endpoint: mongo-b:27017
                  databaseName: game_staging
                  authenticationDatabase: game_staging
                  userName: game_service
                  serverSelectionTimeoutSeconds: 5
                  passwordVariables:
                    profile: GAME_DB_SECRET
                    dbmgr: GAME_DBMGR_SECRET
                redis:
                  endpoint: redis-a:6379
                  userName: cache_service
                  passwordVariable: GAME_CACHE_SECRET
                  profileCachePrefix: "game:staging:profile:"
                """;
            var yaml = core + "\n" + storage;
            File.WriteAllText(path, yaml);
            var settings = ServerConfiguration.Load(path);
            settings.ValidateProfileStorage();
            if (settings.MongoDatabase != "game_staging" || settings.MongoUser != "game_service" ||
                settings.MongoPasswordVariable != "GAME_DB_SECRET" || settings.RedisPasswordVariable != "GAME_CACHE_SECRET" ||
                settings.ProfileCachePrefix != "game:staging:profile:" || settings.DiscoveryRoot != "/game/staging" ||
                settings.ProfileMongo != "mongo-b:27017" ||
                settings.ProfileRedis != "redis-a:6379" || settings.RedisUser != "cache_service" || settings.ZooKeeper != "zk-a:2181")
            {
                throw new Exception("Selected environment settings were replaced by fixed test values.");
            }

            File.WriteAllText(path, core);
            var coreSettings = ServerConfiguration.Load(path);
            if (coreSettings.ZooKeeper != "zk-a:2181" || coreSettings.DiscoveryRoot != "/game/staging")
            {
                throw new Exception("The process framework requires only ZooKeeper settings.");
            }

            try
            {
                coreSettings.ValidateProfileStorage();
                throw new Exception("Incomplete optional storage settings were accepted by the storage entry.");
            }
            catch (InvalidDataException)
            {
            }

            foreach (var invalid in new[] { "", "mongoDatabase: only-one-setting", yaml.Replace("/game/staging", "relative-root") })
            {
                File.WriteAllText(path, invalid);
                try
                {
                    ServerConfiguration.Load(path);
                    throw new Exception("Invalid server configuration was accepted.");
                }
                catch (InvalidDataException)
                {
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
