# ProfileStore.cs 代码详解

[源码](../../../../../../code/server/logic/storage/ProfileStore.cs)

## 调用入口和数据归属

`ProfileStore` 保存实体展示摘要。独立工具显式调用 `OpenAsync(settings, ids, log, stop)` 获得存储对象，再调用 `HandleAsync(request, stop)`。MongoDB 的 `entity_profiles` 集合保存权威数据，Redis 缓存常查摘要。当前 App 不创建这个组件，进程 RPC 也未接入 Profile 业务。 [业务协议详解](../../../shared/protocols/profile.proto.md)

| 成员 | 作用与生命周期 |
| --- | --- |
| `mongo`、`redis` | 存储对象拥有的 Mongo 客户端和 Redis 多路复用连接。成功打开后跨多个请求复用，由 `Dispose` 释放 |
| `summaries`、`cache` | 从上述客户端获得的数据库操作入口。 `summaries` 指向配置数据库中的固定集合，`cache` 来自 Redis 默认数据库 |
| `cacheKeyPrefix` | 创建时从配置读入。给定前缀 `profile:` 与实体 ID `42`，键是 `profile:42` |
| `log`、`ids` | 外部提供的日志和 ID 生成器。前者记录操作及降级事件，后者生成缓存占位 token。调用者管理它们的生命周期 |
| `CacheReservationLifetime`、`CacheLifetime` | 占位 token 保留 5 秒，成功读取回填的摘要保留 1 分钟。占位只协调并发读取 |

## 打开顺序

1. `settings.ValidateProfileStorage()` 校验 Mongo、Redis 及 Profile 凭据的配置。Redis 的地址、用户名来自配置，密码由 `StorageConnections.Password` 按环境变量名查找。 `EndPoints = { settings.ProfileRedis }` 是对已有集合添加元素的初始化语法。
2. 构建 Redis 选项时将连接和异步命令超时设为 2000 毫秒，`ConnectRetry` 设为 1，`AbortOnConnectFail` 设为 `false`。Redis 可以在初始连接未成功时保留连接对象继续尝试。打开返回后，操作仍需处理 Redis 不可用。
3. `StorageConnections.Mongo` 创建客户端，`MongoStorage.CheckConnectionAsync` 对业务数据库发 `ping`，把 `stop` 传给 Mongo 驱动。通过后才等待 `ConnectionMultiplexer.ConnectAsync(options)`。这个 Redis 调用没有接收 `stop`。
4. 私有构造函数接收已创建的连接，拿到集合与缓存句柄。Mongo 客户端创建后的 `try` 如发生异常，`catch` 先释放 Mongo，再用 `throw;` 原样重抛。正常返回时客户端所有权转给 `ProfileStore`。

日志会逐步记录存储配置加载、Mongo 客户端创建、Mongo 连接检查和 Redis 客户端初始化。这样可以从最近一条已完成事件看出故障发生在哪一段。 [连接组装详解](StorageConnections.cs.md) · [Mongo 连接检查详解](MongoStorage.cs.md)

## `Subject` 和 `HandleAsync` 的共用部分

`Subject` 是 `switch` 表达式，根据 Protobuf 的 `OperationCase` 取 ID。 `Publish` 从摘要的 `EntityId` 取，`Fetch` 和 `Withdraw` 本身就是 ID 字符串，其他情况返回空字符串。

`HandleAsync` 用 ID 拼接 Redis 键，并构造 Mongo 的 `_id == id` 筛选器。它预填回复的 `RequestId` 和 `EntityId`，再让各分支写入 `Result` 和必要的 `Summary`。操作未设置或无法识别时，最终抛出 `InvalidDataException`。这里信任上层传入的 ID 和操作权限，直接调用该方法前仍需由调用方完成业务校验。

## `Fetch` 的命中、回源和回填

1. 先用 `ids.GenId()` 生成带 `!` 前缀的候选 token，再用 `StringGetAsync` 读取 Redis 键。字节数组存在、有内容且首字节不是 `!` 时，按 `EntitySummary` 反序列化，核对 `EntityId`。匹配就写入 `Summary`、返回 `Found` 并记录 `CacheHit`。解析失败或 ID 不一致会抛出异常。
2. 仅在 Redis 返回 `null` 时尝试 `StringSetAsync(..., when: When.NotExists)`。这是 Redis 的条件写入，只有键仍不存在才写入 5 秒 token。键已有另一个读者的占位或为空字节数组时，当前调用也会继续查 Mongo。未取得自己占位的调用不会回填。
3. `Find(filter).FirstOrDefaultAsync(stop)` 异步查询 Mongo。文档存在时从 `display_name`、`title` 重建 `EntitySummary` 并设置 `Found`，没有文档时返回 `Missing`。
4. 当前调用取得占位后，用 Redis 事务登记 `Condition.StringEqual(key, token)`，再登记一项写入。Mongo 有文档就写入 Protobuf 摘要和 1 分钟 TTL，否则删除占位。 `ExecuteAsync` 检查条件并执行事务。条件成立后才等待已登记的写任务 `mutation`。查询结果来自前一步 Mongo 读取，即使回填条件失败，仍可把这次读到的结果交给调用者。

`byte[]?` 表示数组引用允许为空。 `bytes is { Length: > 0 }` 同时要求引用存在且长度大于零，所以后面的 `bytes[0]` 有元素可读。Redis 键里的 `!` 字符串与正常 Protobuf 字节共用同一键，代码通过首字节区分两者。

### 并发例子

读者 A 先得到占位 `!A`，从 Mongo 读到标题“旧”。写者 B 把标题更新为“新”，然后删除 Redis 键。A 再尝试回填时，Redis 中已经没有 `!A`。即使读者 C 已写入新占位，值也会是 `!C`。事务条件失败，A 的旧标题留在 A 的这一次响应里，后续读者不会读到由 A 重新填入的旧缓存。占位超时也会使该事务条件失效。

## `Publish`、`Withdraw` 和失败处理

| 操作 | Mongo 动作 | 正常返回 |
| --- | --- | --- |
| `Publish` | 构造只含 `_id`、`display_name`、`title` 的 BSON 文档，用 `ReplaceOneAsync` 替换。 `IsUpsert = true` 使没有匹配文档时插入新文档 | `Stored` |
| `Withdraw` | 用 `DeleteOneAsync` 删除。代码未检查删除数量，删除操作正常返回时即给 `Removed` | `Removed` |

两种操作均先等待 Mongo 完成，再用 `KeyDeleteAsync` 删除对应缓存，并记录最终业务操作日志。Redis 连接或超时异常由 `catch (...) when (...)` 过滤捕获，记为 `CacheUnavailable`。读缓存失败后走 Mongo，回填失败时仍返回 Mongo 读取结果，写操作删缓存失败时仍返回已完成的 Mongo 结果。若缓存删除失败，已缓存的旧摘要可能继续可见直到 TTL 到期。

Mongo 读写失败和取消会继续向调用方传播，Redis 命令靠驱动超时收束。Mongo 读取在回填前失败时，已取得的占位等待 5 秒 TTL 清除。查询过程没有把 Redis 和 Mongo 合成原子操作，读者在并发写入的时间窗口内可能读到旧值。

## 释放与阅读验证

调用者可用 `using var store = await ProfileStore.OpenAsync(...);`。离开作用域时调用同步 `Dispose`，先释放 Redis 连接，再释放 Mongo 客户端。外部提供的 `GameLog` 和 `IdGenerator` 由外部释放。

`ProfileStorageChecks` 依次检查缺失、首次写入、两次读取、更新后读取、撤回与再次缺失，并在 `finally` 用独立超时清理测试记录。 [ProfileStorageChecks 源码](../../../../../../code/tests/engine/ProfileStorageChecks.cs)
