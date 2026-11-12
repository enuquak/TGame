using TGame;
using TGame.Server;

internal static class SettingsChecks
{
    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tgame-settings-{Guid.NewGuid():N}.yaml");
        var previous = Environment.GetEnvironmentVariable(ServerEnvironment.Diagnostics);
        try
        {
            const string yaml = """
                dbMgrMongo: mongo-a:27017
                profileMongo: mongo-b:27017
                profileRedis: redis-a:6379
                zooKeeper: zk-a:2181
                mongoDatabase: game_staging
                mongoUser: game_service
                mongoPasswordVariable: GAME_DB_SECRET
                redisUser: cache_service
                redisPasswordVariable: GAME_CACHE_SECRET
                profileCachePrefix: "game:staging:profile:"
                discoveryRoot: /game/staging
                """;
            File.WriteAllText(path, yaml);
            var settings = ServerSettings.Load(path);
            if (settings.MongoDatabase != "game_staging" || settings.MongoUser != "game_service" ||
                settings.MongoPasswordVariable != "GAME_DB_SECRET" || settings.RedisPasswordVariable != "GAME_CACHE_SECRET" ||
                settings.ProfileCachePrefix != "game:staging:profile:" || settings.DiscoveryRoot != "/game/staging" ||
                settings.DbMgrMongo != "mongo-a:27017" || settings.ProfileMongo != "mongo-b:27017" ||
                settings.ProfileRedis != "redis-a:6379" || settings.RedisUser != "cache_service" || settings.ZooKeeper != "zk-a:2181")
            {
                throw new Exception("Selected environment settings were replaced by fixed test values.");
            }

            foreach (var invalid in new[] { "", "mongoDatabase: only-one-setting", yaml.Replace("/game/staging", "relative-root") })
            {
                File.WriteAllText(path, invalid);
                try
                {
                    ServerSettings.Load(path);
                    throw new Exception("Invalid server configuration was accepted.");
                }
                catch (InvalidDataException)
                {
                }
            }

            Environment.SetEnvironmentVariable(ServerEnvironment.Diagnostics, null);
            if (ServerSettings.DiagnosticsEnabled())
            {
                throw new Exception("Diagnostics must be disabled by default.");
            }

            Environment.SetEnvironmentVariable(ServerEnvironment.Diagnostics, "true");
            if (!ServerSettings.DiagnosticsEnabled())
            {
                throw new Exception("Explicit diagnostic opt-in was ignored.");
            }

            Environment.SetEnvironmentVariable(ServerEnvironment.Diagnostics, "invalid");
            try
            {
                ServerSettings.DiagnosticsEnabled();
                throw new Exception("Invalid diagnostic opt-in was silently accepted.");
            }
            catch (InvalidOperationException)
            {
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServerEnvironment.Diagnostics, previous);
            File.Delete(path);
        }
    }
}
