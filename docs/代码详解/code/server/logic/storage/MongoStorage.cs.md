# MongoStorage.cs 代码详解

[源码](../../../../../../code/server/logic/storage/MongoStorage.cs)

## 两个分开的动作

`MongoStorage` 是程序集内部的静态工具类。 `CreateClient` 把配置翻译成 MongoDB 驱动选项并创建客户端，`CheckConnectionAsync` 发送命令验证客户端目前能否完成一次数据库操作。独立 Profile 工具先经 `StorageConnections.Mongo` 调用前者，再对所选业务数据库调用后者。当前 App 启动流程不调用这两个方法。 [StorageConnections 详解](StorageConnections.cs.md) · [ProfileStore 详解](ProfileStore.cs.md)

## `CreateClient(settings, password)`

| 步骤 | 代码 | 含义 |
| --- | --- | --- |
| 检查实参 | `ThrowIfNull(settings)` 和 `ThrowIfNullOrWhiteSpace(password)` | 及早暴露缺失配置或空白密码 |
| 检查配置 | `settings.Validate()` | 要求端点、业务库、认证库、用户名都有内容，服务器选择超时大于零 |
| 解析端点 | `MongoServerAddress.Parse(settings.Endpoint)` | 将配置中的地址交给驱动解释 |
| 构造认证信息 | `MongoCredential.CreateCredential(settings.AuthenticationDatabase, settings.UserName, password)` | 指定驱动到哪个库验证账号 |
| 配置等待时间 | `TimeSpan.FromSeconds(settings.ServerSelectionTimeoutSeconds)` | 将配置的秒数转换成驱动接受的时长，控制选择可服务服务器的等待时间 |
| 返回客户端 | `new MongoClient(options)` | 把客户端交给调用者持有 |

`new MongoClientSettings { ... }` 是对象初始化器，先创建选项对象，再依次给列出的属性赋值。这里的业务数据库名参与配置校验，真正选库发生在调用者执行 `client.GetDatabase(settings.MongoDatabase)` 时。比如账号记录在 `admin`、摘要写入 `profiles` 时，认证数据库填前者，业务数据库填后者。客户端构造成功后，仍需执行数据库命令来确认当前网络和账号配置能否使用。

## `CheckConnectionAsync(database, stop)`

```csharp
var command = new BsonDocument(PingCommand, 1);
var reply = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: stop);
```

`BsonDocument` 是驱动表示 BSON 文档的类型。这里创建的命令等价于 `{ ping: 1 }`。 `IMongoDatabase` 表示选定的数据库，调用者从客户端获得它后传入。 `RunCommandAsync<BsonDocument>` 的类型参数指定让驱动把回复解析成 BSON 文档，`cancellationToken: stop` 是命名实参。 `await` 异步等待响应，完成后才检查 `reply["ok"].ToDouble()` 是否为 1。

- 回复中的 `ok` 可转为数值但不等于 1 时，显式抛出 `IOException`。字段缺失或无法转换时，索引或转换异常也会向上传递。
- 驱动报告连接超时、认证错误或取消时，方法没有捕获并替换异常。调用者等待该任务时会观察到失败。
- `ping` 验证这一次命令，后续集合读写仍有各自的权限、可用性和错误结果。该方法也不打开、关闭或释放传入的数据库对象。

## 客户端生命周期与检查入口

创建客户端的调用者负责释放。 `ProfileStore.OpenAsync` 创建客户端后，如果连通性检查或随后初始化失败，会在 `catch` 中释放 Mongo 客户端并重新抛出错误。初始化完成的 `ProfileStore` 在 `Dispose` 中释放客户端。

本地 `MongoStorageChecks` 使用不可服务的回环端点，覆盖驱动选项、取消和服务器选择超时。独立的 `ProfileStorageChecks` 调用 `ProfileStore` 完成实际的数据库与缓存读写。 [MongoStorageChecks 详解](../../../tests/engine/MongoStorageChecks.cs.md) · [ProfileStorageChecks 源码](../../../../../../code/tests/engine/ProfileStorageChecks.cs)
