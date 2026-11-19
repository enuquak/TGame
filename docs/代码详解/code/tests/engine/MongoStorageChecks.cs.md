# MongoStorageChecks.cs 代码详解

源码：[code/tests/engine/MongoStorageChecks.cs](../../../../../code/tests/engine/MongoStorageChecks.cs)

## 职责与执行条件

`RunAsync()` 检查独立 MongoDB 配置映射、参数拒绝和连接失败行为。它在回环随机端口启动普通 `TcpListener`，让端口存在但不提供 MongoDB 协议；不需要真实 MongoDB，也不会写入数据库。固定密码 `storage-check-only` 仅用于本地构造凭据的测试数据。

## 关键流程

先加载输出目录的完整 `ServerConfiguration`，由本地 `SaveSettings` 仅替换 `mongo` 节，保留有效的核心配置、数量与拓扑。将监听地址、数据库 `configured_database`、认证库 `configured_auth`、用户 `configured_user` 和一秒服务选择超时写入临时 YAML，再通过 `MongoStorageSettings.Load`、`MongoStorage.CreateClient` 创建客户端。

测试直接读取驱动设置，要求 endpoint、认证来源、用户名、数据库名称和超时时间均来自配置。调用 `GetDatabase` 只用于核对数据库命名空间，本身不代表已经连接成功。

使用预取消令牌调用 `CheckConnectionAsync`，要求立即以 `OperationCanceledException` 结束；随后以五秒外层期限调用，要求面对非 MongoDB 端口产生 `TimeoutException`，验证驱动的一秒服务选择失败能够明确传播。

最后分别检查空密码、空数据库名、零服务选择超时被拒绝，前两项要求 `ArgumentException`，超时要求 `ArgumentOutOfRangeException`。

## 清理与限制

Mongo 客户端放在 `using` 中；无论成功或失败，`finally` 都停止本地监听并删除临时文件。`ExpectAsync<TException>` 同时观察回调同步抛错和异步失败，仅接受预期异常类型。

这份检查证明配置被传入驱动且失败没有被吞掉，不验证真实服务认证成功、CRUD、索引或性能。它与需要真实 MongoDB/Redis 的 `ProfileStorageChecks` 是不同范围。
