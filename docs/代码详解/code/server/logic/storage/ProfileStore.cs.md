# ProfileStore.cs 代码详解

[查看源码](../../../../../../code/server/logic/storage/ProfileStore.cs)

`ProfileStore` 是保留的独立业务存储组件：MongoDB 是摘要权威数据源，Redis 是短时读取缓存。当前 App 不在启动时创建它，也没有把它接入进程 RPC；使用方必须显式调用 `OpenAsync` 并负责释放。

## 创建和依赖

`OpenAsync` 先校验 Profile 存储设置，从环境变量取得 Redis 与 Mongo 密码，使用配置中的 Mongo 认证数据库和超时创建客户端并执行 ping，再创建 Redis 多路复用连接。Mongo 初始化后的任意打开失败都会释放 Mongo 客户端。

构造函数保存注入的 `GameLog` 和 `IdGenerator`，但不拥有它们；它自身拥有 MongoClient 和 ConnectionMultiplexer。集合名为 `entity_profiles`，键前缀来自配置。Redis 使用 2 秒连接/异步超时和一次连接重试。

## 请求与数据库模型

`Subject` 从 Publish、Fetch 或 Withdraw 操作取实体 ID。`HandleAsync` 为请求构造 `_id` 过滤器和带原 RequestId/EntityId 的响应。Publish 以 upsert 替换文档，写入 `_id`、`display_name`、`title`；Withdraw 删除匹配文档。当前方法假定上层已完成业务身份等校验，不负责网络鉴权或实体生命周期。

## Fetch 与缓存竞态

读取先检查 Redis。正常缓存为 EntitySummary 的 Protobuf 字节；开头为 `!` 的值是缓存回填占位。有效缓存还要验证 EntityId 与请求相同，然后直接返回 Found。

缓存不存在时，读取方用 `!` 加生成的唯一 ID 做 5 秒占位，且只在键不存在时写入。之后读取 Mongo。取得占位的读取方用 Redis 事务检查键仍等于自己的 token，再写入 1 分钟缓存，或在数据库不存在记录时删除占位。

这样并发写入方在修改 Mongo 后删除缓存键，会同时破坏旧读取方的占位条件；旧读取方不能把修改前的数据重新填回缓存。未取得占位的读者仍可直接查 Mongo，但不竞争回填。

## 写入、降级与释放

Publish/Withdraw 完成 Mongo 操作后尝试删除 Redis 缓存，再返回 Stored/Removed。Redis 连接或超时异常会记录 `CacheUnavailable`，Fetch 回源 Mongo，写操作保留已经完成的数据库结果。其他异常如缓存内容损坏、Mongo 错误不会被这层降级捕获。

缓存失效失败时旧值最多保留到 TTL 到期，因此这里并不承诺严格实时的缓存一致性。取消令牌传给 Mongo 操作；Redis 调用使用驱动的超时设置。

`Dispose` 释放 Redis 和 Mongo，自身不释放外部注入的日志与 ID 服务。理解这层所有权，才能避免独立工具关闭时误伤进程共享依赖。
