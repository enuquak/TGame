namespace TGame;

public static class ServerEnvironment
{
    public const string GMToken = "TGAME_GM_TOKEN";
    public const string GMOperator = "TGAME_GM_OPERATOR";
    public const string LogRoot = "TGAME_SERVER_LOG_ROOT";
    public const string MongoTestPassword = "TGAME_MONGO_TEST_PASSWORD";
    public const string RedisTestPassword = "TGAME_REDIS_TEST_PASSWORD";
}

public static class ServerPeerNames
{
    public const string Client = "Client";
    public const string MongoDB = "MongoDB";
}
