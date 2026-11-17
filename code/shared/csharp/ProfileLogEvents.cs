namespace TGame;

public static class ProfileLogEvents
{
    public const string Prefix = "PROFILE_";
    public const string RequestTx = "PROFILE_REQUEST_TX";
    public const string RequestRx = "PROFILE_REQUEST_RX";
    public const string ReplyTx = "PROFILE_REPLY_TX";
    public const string ReplyRx = "PROFILE_REPLY_RX";
    public const string DeliveryFailed = "PROFILE_DELIVERY_FAILED";
    public const string DbError = "PROFILE_DB_ERROR";
    public const string CacheHit = "PROFILE_CACHE_HIT";
    public const string CacheUnavailable = "PROFILE_CACHE_UNAVAILABLE";
    public const string Missing = "PROFILE_MISSING";
    public const string MongoRead = "PROFILE_MONGO_READ";
    public const string Stored = "PROFILE_STORED";
    public const string Removed = "PROFILE_REMOVED";
}
