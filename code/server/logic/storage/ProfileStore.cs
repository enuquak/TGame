using Google.Protobuf;
using MongoDB.Bson;
using MongoDB.Driver;
using TGame.Server;
using StackExchange.Redis;
using TGame.Profile;
using Result = TGame.Profile.ProfileReply.Types.Result;
using TGame.Server.Engine;
using TGame.Server.Storage;

namespace TGame.Server.Logic;

internal sealed class ProfileStore : IDisposable
{
    private const string CollectionName = "entity_profiles";
    private readonly string cacheKeyPrefix;
    private readonly GameLog log;
    private readonly IdGenerator ids;
    private const string EntityIdField = "_id";
    private const string DisplayNameField = "display_name";
    private const string TitleField = "title";
    private const char CacheReservationMarker = '!';
    private const int RedisTimeoutMilliseconds = 2000;
    private const int RedisConnectRetryCount = 1;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CacheReservationLifetime = TimeSpan.FromSeconds(5);

    private readonly IDatabase cache;
    private readonly MongoClient mongo;
    private readonly ConnectionMultiplexer redis;
    private readonly IMongoCollection<BsonDocument> summaries;

    private ProfileStore(MongoClient mongo, ConnectionMultiplexer redis, ServerSettings settings, IdGenerator ids, GameLog log)
    {
        this.log = log;
        this.ids = ids;
        cacheKeyPrefix = settings.ProfileCachePrefix;
        this.mongo = mongo;
        this.redis = redis;
        summaries = mongo.GetDatabase(settings.MongoDatabase).GetCollection<BsonDocument>(CollectionName);
        cache = redis.GetDatabase();
    }

    public static async Task<ProfileStore> OpenAsync(ServerSettings settings, IdGenerator ids, GameLog log, CancellationToken stop)
    {
        settings.ValidateProfileStorage();
        var options = new ConfigurationOptions
        {
            EndPoints = { settings.ProfileRedis },
            User = settings.RedisUser,
            Password = StorageConnections.Password(settings.RedisPasswordVariable),
            AbortOnConnectFail = false,
            ConnectTimeout = RedisTimeoutMilliseconds,
            AsyncTimeout = RedisTimeoutMilliseconds,
            ConnectRetry = RedisConnectRetryCount
        };

        log.Info(ServerLogEvents.InitStepCompleted, nameof(ProcessType.Profile), detail: "Profile 存储配置加载完成");

        var mongo = StorageConnections.Mongo(settings);
        try
        {
            log.Info(ServerLogEvents.InitStepCompleted, nameof(ProcessType.Profile), detail: "MongoDB 客户端创建完成");

            await MongoStorage.CheckConnectionAsync(mongo.GetDatabase(settings.MongoDatabase), stop);
            log.Info(ServerLogEvents.InitStepCompleted, nameof(ProcessType.Profile), detail: "MongoDB 连接检查完成");

            var redis = await ConnectionMultiplexer.ConnectAsync(options);
            log.Info(ServerLogEvents.InitStepCompleted, nameof(ProcessType.Profile), detail: "Redis 客户端初始化完成");
            return new ProfileStore(mongo, redis, settings, ids, log);
        }
        catch
        {
            mongo.Dispose();
            throw;
        }
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
        var key = (RedisKey)(cacheKeyPrefix + id);
        var filter = Builders<BsonDocument>.Filter.Eq(EntityIdField, id);
        var reply = new ProfileReply { RequestId = request.RequestId, EntityId = id };

        if (request.OperationCase == ProfileRequest.OperationOneofCase.Fetch)
        {
            // Reserve a cache miss before reading Mongo. A concurrent writer removes this
            // token, so the older reader cannot repopulate a key after invalidation.
            bool reserved = false;
            RedisValue token = CacheReservationMarker + ids.GenId();

            try
            {
                byte[]? bytes = (byte[]?)await cache.StringGetAsync(key);
                if (bytes is { Length: > 0 } && bytes[0] != (byte)CacheReservationMarker)
                {
                    var value = EntitySummary.Parser.ParseFrom(bytes);
                    if (value.EntityId != id)
                    {
                        throw new InvalidDataException("Cached profile entity mismatch.");
                    }

                    reply.Summary = value;
                    reply.Result = Result.Found;
                    log.Debug(ProfileLogEvents.CacheHit, nameof(ProcessType.Profile), id: request.RequestId, entity: id);
                    return reply;
                }

                if (bytes is null)
                {
                    reserved = await cache.StringSetAsync(key, token, CacheReservationLifetime, when: When.NotExists);
                }
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                log.Error(ProfileLogEvents.CacheUnavailable, nameof(ProcessType.Profile),
                    id: request.RequestId, entity: id, detail: ex.GetType().Name);
            }

            var doc = await summaries.Find(filter).FirstOrDefaultAsync(stop);
            if (doc != null)
            {
                reply.Summary = new EntitySummary { EntityId = id, DisplayName = doc[DisplayNameField].AsString, Title = doc[TitleField].AsString };
            }

            reply.Result = doc is null ? Result.Missing : Result.Found;

            if (reserved)
            {
                try
                {
                    var tx = cache.CreateTransaction();
                    tx.AddCondition(Condition.StringEqual(key, token));
                    Task mutation = doc is null ? tx.KeyDeleteAsync(key) : tx.StringSetAsync(key, reply.Summary.ToByteArray(), CacheLifetime);
                    if (await tx.ExecuteAsync())
                    {
                        await mutation;
                    }
                }
                catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
                {
                    log.Error(ProfileLogEvents.CacheUnavailable, nameof(ProcessType.Profile),
                        id: request.RequestId, entity: id, detail: ex.GetType().Name);
                }
            }

            log.Debug(doc is null ? ProfileLogEvents.Missing : ProfileLogEvents.MongoRead,
                nameof(ProcessType.Profile), id: request.RequestId, entity: id);
            return reply;
        }

        if (request.OperationCase == ProfileRequest.OperationOneofCase.Publish)
        {
            var doc = new BsonDocument { { EntityIdField, id }, { DisplayNameField, request.Publish.DisplayName }, { TitleField, request.Publish.Title } };
            await summaries.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true }, stop);
            reply.Result = Result.Stored;
        }
        else if (request.OperationCase == ProfileRequest.OperationOneofCase.Withdraw)
        {
            await summaries.DeleteOneAsync(filter, stop);
            reply.Result = Result.Removed;
        }
        else
        {
            throw new InvalidDataException("Unknown Profile operation.");
        }

        try
        {
            await cache.KeyDeleteAsync(key);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            log.Error(ProfileLogEvents.CacheUnavailable, nameof(ProcessType.Profile),
                id: request.RequestId, entity: id, detail: ex.GetType().Name);
        }

        log.Info(reply.Result == Result.Stored ? ProfileLogEvents.Stored : ProfileLogEvents.Removed,
            nameof(ProcessType.Profile), id: request.RequestId, entity: id);
        return reply;
    }

    public void Dispose()
    {
        redis.Dispose();
        mongo.Dispose();
    }
}
