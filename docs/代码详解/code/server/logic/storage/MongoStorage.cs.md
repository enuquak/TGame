# MongoStorage.cs 代码详解

[查看源码](../../../../../../code/server/logic/storage/MongoStorage.cs)

`MongoStorage` 将 MongoDB 驱动配置和连通性检查集中起来，供独立存储工具调用。它没有持有静态连接，也不会在 App 启动时被自动调用。

`CreateClient(settings, password)` 先检查配置对象、密码以及各字段，再构造 `MongoClientSettings`：`MongoServerAddress.Parse` 解析端点，`MongoCredential.CreateCredential` 使用认证数据库、用户名和外部传入的密码，`ServerSelectionTimeout` 来自配置秒数。返回客户端不等于已经证明数据库在线。

`CheckConnectionAsync(database, stop)` 向指定数据库执行 `{ ping: 1 }`，把取消令牌传给驱动，并要求响应 `ok` 为 1，否则抛 `IOException`。驱动的连接失败、超时和取消异常直接向调用者传播，不伪装成成功。

客户端由创建它的使用方持有和释放。本地 `MongoStorageChecks` 用不可服务的回环端点验证配置、取消和连接超时，不需要真实 MongoDB；真实读写由另一个 Profile 存储检查覆盖。
