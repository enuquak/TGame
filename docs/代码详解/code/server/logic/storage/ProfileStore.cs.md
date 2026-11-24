# ProfileStore.cs 代码详解

[源码](../../../../../../code/server/logic/storage/ProfileStore.cs)

`ProfileStore` 把实体展示摘要保存在 MongoDB，并用 Redis 缓存读取结果。它支持发布、获取、撤回三种存储操作。理解本文件的关键不是记住几个方法名，而是追清每个 `await` 前后：已经改了哪个系统、哪个对象还由自己持有、失败后还能不能返回结果。

当前服务器 App 只运行进程基础框架，不创建实体，也不在启动时初始化这个组件。这里保留的是独立存储能力，已有调用场景见 [ProfileStorageChecks 源码](../../../../../../code/tests/engine/ProfileStorageChecks.cs)。协议的存在不等于当前基础 RPC 已接入 Profile 处理。 [业务协议详解](../../../shared/protocols/profile.proto.md)

## 1. 类声明和类型别名

```csharp
using Result = TGame.Profile.ProfileReply.Types.Result;
```

类声明是：

```csharp
internal sealed class ProfileStore : IDisposable
```

`using Result = ...` 为一个完整类型名取本文件内的短别名。后面的 `Result.Found` 实际使用的是 Protobuf 生成的回复枚举，不是另一个自建枚举。

`internal` 限定程序集内访问，`sealed` 禁止继承这个类，`: IDisposable` 表示它提供同步资源释放方法 `Dispose()`。对象不是静态服务；每次 `OpenAsync` 成功返回的都是一个具体存储实例。

`Google.Protobuf` 提供消息序列化能力；`MongoDB.Bson`/`MongoDB.Driver` 提供文档和数据库操作；`StackExchange.Redis` 提供缓存客户端。这几个库的对象承担不同责任，不能因为方法都带 `Async` 就认为它们使用同一个事务。

## 2. 字段先按所有权分清

| 字段 | 负责什么 | 谁结束其生命周期 |
| --- | --- | --- |
| `mongo` | Mongo 客户端，后续反复执行数据库操作 | 成功打开后由本对象释放 |
| `redis` | Redis 多路复用连接 | 成功打开后由本对象释放 |
| `summaries` | `entity_profiles` 集合的操作入口 | 依托 Mongo 客户端使用，没有单独 Dispose |
| `cache` | Redis 数据库操作入口 | 依托 Redis 连接使用，没有单独 Dispose |
| `log`、`ids` | 外部传入的日志和 ID 服务 | 外部管理，本对象不释放 |
| `cacheKeyPrefix` | 当前存储实例使用的缓存键前缀 | 普通字符串，不需释放 |

`private readonly` 表示字段引用在构造完成后不被重新赋值，不表示被引用对象内部状态不会变化，也不提供线程锁。例如 `readonly MongoClient mongo` 仍可以通过客户端执行数据库操作。

字段名常量明确了存储格式：集合 `entity_profiles`；文档字段 `_id`、`display_name`、`title`。`_id` 的值取请求里的实体 ID 字符串；这里没有把它转成 Mongo 的 `ObjectId`。

缓存有两种值：以 `!` 开头的短期占位 token，以及 Protobuf 编码的摘要字节。占位生存期 5 秒，正常摘要缓存 1 分钟。`static readonly TimeSpan` 表示类型共享的只读时长值；这些时长与连接的 2000 毫秒超时是不同用途的参数。

## 3. 私有构造函数只接纳已经准备好的依赖

```csharp
private ProfileStore(MongoClient mongo, ConnectionMultiplexer redis, ServerConfiguration settings, IdGenerator ids, GameLog log)
{
    this.log = log;
    this.ids = ids;
    cacheKeyPrefix = settings.ProfileCachePrefix;
    this.mongo = mongo;
    this.redis = redis;
    summaries = mongo.GetDatabase(settings.MongoDatabase).GetCollection<BsonDocument>(CollectionName);
    cache = redis.GetDatabase();
}
```

`this.mongo = mongo` 左边是当前对象的字段，右边是同名形参；`this` 用来消除歧义。这里保存已有客户端引用，不会复制客户端。

`GetDatabase(...).GetCollection<BsonDocument>(...)` 先选择配置指定的业务库，再取得固定集合的操作入口。泛型参数 `BsonDocument` 指定该集合在此代码里以 BSON 文档读写，而不是自动映射成 `EntitySummary` C# 对象。

`redis.GetDatabase()` 没有显式数据库编号，使用连接配置的默认选择。`cacheKeyPrefix` 从传入配置取出后固定在本实例中，不在每次请求时重读 YAML。

构造函数为 `private`，外部不能绕过工厂随意 `new ProfileStore(...)`。公开的 `OpenAsync` 负责先做异步检查，再调用这个同步构造函数。

## 4. OpenAsync 的第一段：配置校验和 Redis 选项

```csharp
public static async Task<ProfileStore> OpenAsync(ServerConfiguration settings, IdGenerator ids, GameLog log, CancellationToken stop)
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
```

`Task<ProfileStore>` 表示异步成功后提供一个存储对象。调用方写 `await ProfileStore.OpenAsync(...)`，拿到的是对象，不是任务本身。

先调用 `ValidateProfileStorage()`，要求 Mongo/Redis 配置和所需凭据变量名完整。当前服务器基础启动不需要强行具备这些业务存储配置；独立存储入口在真正使用时才做这项校验。

`new ConfigurationOptions { ... }` 是对象初始化器。其中 `EndPoints = { settings.ProfileRedis }` 是对已有端点集合添加元素的初始化写法，不是把字符串直接赋给整个集合。

密码由 [StorageConnections.Password](StorageConnections.cs.md) 按环境变量名读取。`AbortOnConnectFail = false` 配置连接建立行为，但不能把它解释成“Redis 永远不会报错”。本文件后面的每次缓存操作仍然处理 Redis 连接或超时异常。

连接超时和异步命令超时都设为 2000 毫秒，连接重试参数为 1。它们是驱动选项，不构成整个 `OpenAsync` 的两秒硬性上限。

## 5. OpenAsync 的第二段：Mongo 检查先于 Redis

```csharp
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
```

执行顺序严格是：创建 Mongo 客户端 → 等待 Mongo `ping` → 等待 Redis 客户端初始化 → 创建并返回 `ProfileStore`。`await` 不是让这些语句同时发起；第二个异步步骤在第一个成功后才开始。

驱动选项和 `ping` 响应检查逐句见 [MongoStorage 详解](MongoStorage.cs.md)。本文件负责把它接入整个存储对象的初始化顺序。

`nameof(ProcessType.Profile)` 在编译期取得枚举成员名字，避免使用处硬写角色文本。日志中的 `detail:` 是命名实参。每个“完成”日志都位于对应步骤之后，便于识别初始化停在哪一步。

Mongo 检查接收了 `stop`，Redis 的 `ConnectAsync(options)` 没有接收它。因此不能说外部令牌取消会立刻中断这段方法的所有等待。方法也没有最后再调用 `stop.ThrowIfCancellationRequested()`。

裸 `catch` 捕获这个 `try` 内的异常，释放已经创建的 Mongo，然后 `throw;` 重新抛出原异常。它不是返回默认对象，也没有把错误变成日志后吞掉。

资源边界要按源码说完整：失败清理显式覆盖的是 Mongo。若 Redis 对象已经成功返回，而后续日志或私有构造函数抛错，这个 `catch` 没有对该局部 Redis 对象调用 `Dispose()`。因此不能宣称此方法为所有极端初始化失败提供了两个客户端的完整回滚。本文描述现有行为，不修改实现。

## 6. Subject：从 oneof 分支找到实体 ID

```csharp
internal static string Subject(ProfileRequest request) => request.OperationCase switch
{
    ProfileRequest.OperationOneofCase.Publish => request.Publish.EntityId,
    ProfileRequest.OperationOneofCase.Fetch => request.Fetch,
    ProfileRequest.OperationOneofCase.Withdraw => request.Withdraw,
    _ => ""
};
```

这是 `switch` 表达式：先读 `OperationCase`，匹配某一分支，整个表达式产生该分支右边的字符串。末尾 `_` 是兜底匹配。

Protobuf 的 `oneof operation` 让请求表示其中一种操作。`Publish` 的值是摘要消息，所以 ID 在 `request.Publish.EntityId` 中；`Fetch` 和 `Withdraw` 的值就是 ID 字符串。

操作没设置时返回空字符串，不在这个辅助方法里抛错。稍后的 `HandleAsync` 没有匹配到任何合法分支时，才会抛出 `InvalidDataException`。

`Subject` 没有检查 ID 格式、请求权限或实体归属。当前它是独立存储内部入口；不能把“可以构造这个请求对象”当成已经通过业务授权。

## 7. HandleAsync 先准备这次请求的公共数据

```csharp
string id = Subject(request);
var key = (RedisKey)(cacheKeyPrefix + id);
var filter = Builders<BsonDocument>.Filter.Eq(EntityIdField, id);
var reply = new ProfileReply { RequestId = request.RequestId, EntityId = id };
```

这些是每次调用各自的局部变量。`reply` 是新回复对象，先复制请求 ID 和目标实体 ID，具体结果在对应分支中设置。

`(RedisKey)` 是类型转换，把拼接后的字符串变成 Redis 客户端的键类型。例如前缀 `profile:` 与 ID `42` 形成 `profile:42`。这是用于讲解的数据例子，不是项目固定配置。

`Builders<BsonDocument>.Filter.Eq(...)` 创建等值筛选条件，含义是 Mongo 字段 `_id` 等于字符串 `id`。这里只创建筛选描述，没有查询网络。后面的 `Find`、`ReplaceOneAsync`、`DeleteOneAsync` 共用这份筛选条件。

这些请求局部状态各自独立，但底层数据库数据是共享的。源码没有在整个 `HandleAsync` 外包 C# `lock`，不同请求可能在多个 `await` 之间交错。

## 8. Fetch 先生成 token，再尝试读缓存

```csharp
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
```

`ids.GenId()` 在读 Redis 之前执行，且位于这个 `try` 外。即使命中缓存，也会先生成一个候选 token；ID 服务未初始化、已释放或租约失效时，不能靠“缓存命中”跳过发号错误。

`CacheReservationMarker` 是字符 `!`，与 ID 字符串拼接后形成如 `!123...` 的值，再转成 `RedisValue`。它用于区分这次读者的占位，不是实体 ID 本身。

`byte[]?` 表示字节数组引用允许为 null。`bytes is { Length: > 0 }` 同时判断非 null 和至少有一个元素，所以 `&&` 后的 `bytes[0]` 可以安全访问；`&&` 具有短路求值，前半不满足就不访问首字节。

首字节不是 `!` 才尝试按 Protobuf 摘要解析。随后核对摘要自带的 `EntityId`，防止把别的实体缓存当成这个实体的结果。正常时设置 `Summary` 和 `Found`，提前 `return`，后面 Mongo 查询不会执行。

这个判断是项目当前缓存格式约定，不是“任何任意字节都能可靠区分”的通用编码协议。缓存损坏导致的 Protobuf 解析异常或 ID 不匹配异常，不属于下面的 Redis 故障降级捕获范围。

## 9. 缓存缺失时为什么要抢一个短期占位

```csharp
if (bytes is null)
{
    reserved = await cache.StringSetAsync(key, token, CacheReservationLifetime, when: When.NotExists);
}
```

只有 `StringGetAsync` 转出的结果为 null，才尝试写占位。空数组、别人的占位值等情况都继续读 Mongo，但不执行这次占位写入。

`when: When.NotExists` 要求 Redis 执行条件写入：执行时键仍不存在才写入。此前刚读到 null 不代表下一刻仍不存在，因此不能拆成普通的无条件 `StringSetAsync`。条件命令的语义可对照 [Redis 官方 .NET 条件执行说明](https://redis.io/docs/latest/develop/clients/dotnet/condexec/)。

成功写入时 `reserved` 为 true，表示这次请求有资格尝试后续条件回填；失败时为 false，表示别人可能先写了键。这不是让其他读者等待的锁：后续读者遇到占位仍会去 Mongo，并不会等待五秒才查询。

Redis 读取或占位写入发生连接/超时故障时，捕获为：

```csharp
catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
{
    log.Error(ProfileLogEvents.CacheUnavailable, nameof(ProcessType.Profile),
        id: request.RequestId, entity: id, detail: ex.GetType().Name);
}
```

`when` 是异常过滤器，仅匹配列出的类型才进入。`or` 是模式组合。日志记录异常类型名，然后继续 Mongo 路径；没有 catch 的其他异常仍传播。Redis 写入超时也不能简单解释成“服务器端一定没有写”，这里只依据当前调用可观察结果决定是否持有回填资格。

## 10. Mongo 查询到底返回什么

```csharp
var doc = await summaries.Find(filter).FirstOrDefaultAsync(stop);
if (doc != null)
{
    reply.Summary = new EntitySummary { EntityId = id, DisplayName = doc[DisplayNameField].AsString, Title = doc[TitleField].AsString };
}

reply.Result = doc is null ? Result.Missing : Result.Found;
```

`Find(filter)` 组织查询，`FirstOrDefaultAsync(stop)` 异步取得第一个匹配文档，找不到时得到默认引用值 null。这里查 `_id` 等值，目标是一个实体的摘要，不是读出整个集合再在 C# 中筛选。

`doc[DisplayNameField]` 按 Mongo 字段名读值，`.AsString` 要求值可以作为 BSON 字符串读取。代码没有给缺字段或类型错误填默认值；数据结构不符合预期时会失败，不会伪装成 `Missing`。

`new EntitySummary { ... }` 是 Protobuf 对象的属性初始化：ID 来自本次请求，显示名和标题来自 Mongo 文档。非空文档设置 `Found`；null 设置 `Missing`，且没有设置 `Summary`。

`doc is null ? ... : ...` 是一个条件表达式，在两个枚举值中选一个。`Missing` 表示查询正常完成但没找到记录；Mongo 连接失败、查询取消或文档格式错误不会返回这个结果，而是让任务失败。

## 11. 回填之前再次确认占位仍属于自己

```csharp
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
```

只有拿到自己占位的读者进入这里。`CreateTransaction` 创建 Redis 事务对象；`AddCondition` 要求执行时键值仍等于本请求的 token。它只协调 Redis，不把前面的 Mongo 查询纳入同一事务。

`mutation` 保存的是已经登记到事务中的操作任务。没有文档时登记删除占位；有文档时登记把 `Summary.ToByteArray()` 的 Protobuf 字节写入缓存，TTL 为一分钟。`Task<bool>` 等有结果任务可以作为基类 `Task` 保存，这里只需要等待完成，不读取操作返回的布尔值。

事务中的命令先登记，再由 `ExecuteAsync` 尝试执行，所以不能在执行事务前先 `await mutation`。本实现先等待执行结果；返回 true 才等待已登记的操作任务，条件未满足就放弃回填。条件事务的基本语义见 [Redis 官方 .NET 事务说明](https://redis.io/docs/latest/develop/clients/dotnet/transpipe/)。

这一段也只捕获列出的 Redis 故障，记录缓存不可用后继续返回前面 Mongo 查询结果。条件失败本身不表示 Mongo 查询失败，也不会把 `Found` 改成 `Missing`。

### 用一个交错顺序理解条件的作用

下面是概念时序，不是并发测试结果：

```text
读者 A：缓存不存在，写入 !A；从 Mongo 读到标题“旧”
写者 B：Mongo 标题改成“新”；成功删除缓存键
读者 A：要求缓存值仍为 !A，条件不成立，不能回填“旧”
```

即使中间读者 C 已经写入新的 `!C`，A 的条件仍失败。占位五秒到期也使 A 不能再凭旧资格回填。

这只防止该时序中旧读者在成功失效之后重新填入旧值。A 自己这次响应仍可能是“旧”；写者删缓存失败、已有缓存命中、Mongo 与 Redis 之间的时间窗口等都不因此消失。不能把它写成强一致缓存或跨库原子提交。

查询完成后记录 `Missing` 或 `MongoRead` 日志并返回 `reply`。若 Mongo 在此前失败，回填段根本不会执行，已写入的占位依靠五秒 TTL 到期，而不是由 `finally` 立即删除。

## 12. Publish：全量替换，缺失时插入

```csharp
if (request.OperationCase == ProfileRequest.OperationOneofCase.Publish)
{
    var doc = new BsonDocument { { EntityIdField, id }, { DisplayNameField, request.Publish.DisplayName }, { TitleField, request.Publish.Title } };
    await summaries.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true }, stop);
    reply.Result = Result.Stored;
}
```

BSON 初始化器中每个 `{ 字段名, 值 }` 添加一个文档元素。本次构造的文档只含 `_id`、`display_name`、`title` 三项。

`ReplaceOneAsync` 是替换匹配文档，不是 `$set` 式仅修改列出的字段。`IsUpsert = true` 表示找不到匹配文档时插入替换文档。相关 API 语义见 [MongoDB 官方 C# 替换文档说明](https://www.mongodb.com/docs/drivers/csharp/current/crud/replace/)。

概念例：若原记录是 `{_id: "42", display_name: "旧名", title: "新人", extra: "历史字段"}`，发布的新摘要构造出 `{_id: "42", display_name: "新名", title: "队长"}`，替换后不会因为旧文档有 `extra` 就自动保留它。若原先没有 `42`，则走 upsert 插入。

这条 `await` 的结果对象被丢弃。代码没有根据 `MatchedCount`、`ModifiedCount` 或 `UpsertedId` 区分新建、更新、内容不变；驱动调用正常返回后都设置 `Stored`。所以 `Stored` 不能解释成“恰好修改了一条既有记录”。

`stop` 传给 Mongo 驱动。异常或取消会跳过 `reply.Result = ...` 及后面的缓存失效步骤，向调用方传播；发生失败时不能由这个方法推导远端写入必定从未发生。

## 13. Withdraw：删除请求完成，不等于原先一定存在

```csharp
else if (request.OperationCase == ProfileRequest.OperationOneofCase.Withdraw)
{
    await summaries.DeleteOneAsync(filter, stop);
    reply.Result = Result.Removed;
}
else
{
    throw new InvalidDataException("Unknown Profile operation.");
}
```

`DeleteOneAsync` 以同一个 `_id` 筛选器删除匹配文档。源码没有读取删除结果里的数量。正常返回后设置 `Removed`，因此“原来存在并被删掉”和“原来已经不存在”在这层得到同一个业务结果。

这使重复撤回在业务回复上不必报“找不到”，但不能把 `Removed` 当成一条数据库受影响行数的证明。

最后的 `else` 对未知或未设置操作抛错。`Subject` 对这种请求返回的空 ID 并没有让它变成一次合法空键写操作。

## 14. 两种写操作随后共用缓存失效

```csharp
try
{
    await cache.KeyDeleteAsync(key);
}
catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
{
    log.Error(ProfileLogEvents.CacheUnavailable, nameof(ProcessType.Profile),
        id: request.RequestId, entity: id, detail: ex.GetType().Name);
}
```

先完成 Mongo 发布/删除，再删除 Redis 键。删除的是正常缓存或读者占位中的任一种当前值；这也是旧读者之后必须检查 token 的原因。

Redis 删除结果没有用于改变业务结果，键原本不存在也继续完成。指定的 Redis 故障被记录后，仍返回已经取得的 `Stored` 或 `Removed`。因此缓存不可用可以降级，但 Mongo 失败不能被这段 catch 吞掉：Mongo 操作在该 try 之前。

若 Mongo 已更新而缓存删除失败，旧摘要可能继续被命中，直到它的 TTL 到期或后续被删除。代码没有后台重试队列、版本号验证或跨库回滚。

最后的 `log.Info` 根据 `reply.Result` 在 `Stored` 与 `Removed` 日志事件中选择，再返回回复。日志也属于实际执行语句，不能从某个数据库 await 完成推导整个方法必然正常返回。

## 15. 取消、线程与释放的完整边界

本文件的 Mongo `ping`、查询、替换和删除都接收 `stop`；Redis 初始化和缓存调用没有接收这个令牌。没有统一的 `Task.Run` 把方法扔到专用线程，也没有 `ConfigureAwait(false)` 保证避开调用方上下文。`await` 表示异步等待，不表示请求之间串行。

```csharp
public void Dispose()
{
    redis.Dispose();
    mongo.Dispose();
}
```

同步释放先 Redis、后 Mongo。不释放注入的 `ids` 或 `log`。方法没有显式等待仍在进行的 `HandleAsync`，调用方应先收束使用，再结束存储对象生命周期；也不能声称这里提供了 Dispose 与请求并发执行的协调。

概念用法是 `using var store = await ProfileStore.OpenAsync(...);`：先异步得到对象，离开作用域时执行同步 `Dispose()`。这里用 `using`，不是 `await using`，因为类实现的是 `IDisposable`。

释放语句没有 `try/finally`：若第一个释放调用异常，第二个不会由本方法保证执行。源码也没有自己的 `disposed` 状态保护，重复释放语义依赖其调用到的客户端实现，不应替它承诺。

按 [ProfileStorageChecks 源码](../../../../../../code/tests/engine/ProfileStorageChecks.cs) 可串读缺失、发布、读取、更新、撤回等具体请求。阅读这些场景能核对业务结果，但本文没有运行实际数据库或 Redis 测试，也不把顺序用例当作并发一致性的完整证明。
