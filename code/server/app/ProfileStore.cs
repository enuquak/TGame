using Google.Protobuf;
using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;
using TGame.Profile;
using Result = TGame.Profile.ProfileReply.Types.Result;

namespace TGame.PingPong;

// The smoke host runs in the test namespace over the project's SSH tunnel.
internal sealed class ProfileStore : IDisposable
{
    private readonly IMongoCollection<BsonDocument> summaries;
    private readonly MongoClient mongo;
    private readonly ConnectionMultiplexer redis;
    private readonly IDatabase cache;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);

    public ProfileStore()
    {
        var settings = TestStorage.Settings.Value;
        mongo = TestStorage.Mongo(settings.ProfileMongo);
        summaries = mongo.GetDatabase("tgame_test").GetCollection<BsonDocument>("entity_profiles");
        redis = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = { settings.ProfileRedis }, User = "tgame_test", Password = TestStorage.Password("TGAME_REDIS_TEST_PASSWORD"),
            AbortOnConnectFail = false, ConnectTimeout = 2000, AsyncTimeout = 2000
        });
        cache = redis.GetDatabase();
    }

    internal static string Subject(ProfileRequest request) => request.OperationCase switch
    {
        ProfileRequest.OperationOneofCase.Publish => request.Publish.EntityId,
        ProfileRequest.OperationOneofCase.Fetch => request.Fetch,
        ProfileRequest.OperationOneofCase.Withdraw => request.Withdraw,
        _ => ""
    };

    internal async Task<ProfileReply> HandleAsync(ProfileRequest request, CancellationToken stop)
    {
        string id = Subject(request);
        var key = (RedisKey)("tgame:test:profile:" + id);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        var reply = new ProfileReply { RequestId = request.RequestId, EntityId = id };
        if (request.OperationCase == ProfileRequest.OperationOneofCase.Fetch)
        {
            // Reserve a cache miss before reading Mongo. A concurrent writer removes this
            // token, so the older reader cannot repopulate a key after invalidation.
            RedisValue token = "!" + Guid.NewGuid().ToString("N");
            bool reserved = false;
            try
            {
                byte[]? bytes = (byte[]?)await cache.StringGetAsync(key);
                if (bytes is { Length: > 0 } && bytes[0] != (byte)'!')
                {
                    var value = EntitySummary.Parser.ParseFrom(bytes);
                    if (value.EntityId != id) throw new InvalidDataException("Cached profile entity mismatch.");
                    reply.Summary = value; reply.Result = Result.Found;
                    Log("CACHE_HIT");
                    return reply;
                }
                if (bytes is null)
                    reserved = await cache.StringSetAsync(key, token, TimeSpan.FromSeconds(5), when: When.NotExists);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException) { Log("CACHE_UNAVAILABLE", ex.GetType().Name); }
            var doc = await summaries.Find(filter).FirstOrDefaultAsync(stop);
            if (doc != null)
                reply.Summary = new EntitySummary { EntityId = id, DisplayName = doc["display_name"].AsString, Title = doc["title"].AsString };
            reply.Result = doc is null ? Result.Missing : Result.Found;
            if (reserved)
            {
                try
                {
                    var tx = cache.CreateTransaction();
                    tx.AddCondition(Condition.StringEqual(key, token));
                    Task mutation = doc is null ? tx.KeyDeleteAsync(key) : tx.StringSetAsync(key, reply.Summary.ToByteArray(), CacheLifetime);
                    if (await tx.ExecuteAsync()) await mutation;
                }
                catch (Exception ex) when (ex is RedisException or RedisTimeoutException) { Log("CACHE_UNAVAILABLE", ex.GetType().Name); }
            }
            Log(doc is null ? "MISSING" : "MONGO_READ");
            return reply;
        }
        if (request.OperationCase == ProfileRequest.OperationOneofCase.Publish)
        {
            var doc = new BsonDocument { { "_id", id }, { "display_name", request.Publish.DisplayName }, { "title", request.Publish.Title } };
            await summaries.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true }, stop);
            reply.Result = Result.Stored;
        }
        else if (request.OperationCase == ProfileRequest.OperationOneofCase.Withdraw)
        {
            await summaries.DeleteOneAsync(filter, stop);
            reply.Result = Result.Removed;
        }
        else throw new InvalidDataException("Unknown Profile operation.");
        try { await cache.KeyDeleteAsync(key); }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException) { Log("CACHE_UNAVAILABLE", ex.GetType().Name); }
        Log(reply.Result == Result.Stored ? "STORED" : "REMOVED");
        return reply;

        void Log(string action, string? detail = null) =>
            TraceLog.Event("PROFILE_" + action, "Profile", id: request.RequestId, entity: id, detail: detail);
    }

    public void Dispose() { redis.Dispose(); mongo.Dispose(); }
}
