namespace TGame;

public static class ServerEnvironment
{
    public const string LogRoot = "TGAME_SERVER_LOG_ROOT";
    public const string DBMgrMongoPassword = "TGAME_DBMGR_MONGO_PASSWORD";
    public const string Config = "TGAME_SERVER_CONFIG";
    public const string Diagnostics = "TGAME_SERVER_DIAGNOSTICS";
    public const string MongoPassword = "TGAME_MONGO_PASSWORD";
    public const string RedisPassword = "TGAME_REDIS_PASSWORD";
}

public static class ServerPeerNames
{
    public const string Client = "Client";
    public const string MongoDB = "MongoDB";
}
