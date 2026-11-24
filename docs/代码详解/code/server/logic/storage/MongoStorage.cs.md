# MongoStorage.cs 代码详解

[源码](../../../../../../code/server/logic/storage/MongoStorage.cs)

这份文件把两个动作分开：`CreateClient` 创建驱动客户端，`CheckConnectionAsync` 使用选定数据库执行一次 `ping`。前者成功返回不等于后者已经成功。读后面的 [ProfileStore](ProfileStore.cs.md) 时，这个区别能解释为什么初始化日志里有“客户端创建完成”和“连接检查完成”两步。

当前 App 不在启动阶段调用这个存储工具。配置类型位于 Engine，驱动使用和存储实现位于 logic；这里属于后者。

## 1. 类型与两个常量

```csharp
using MongoDB.Bson;
using MongoDB.Driver;

namespace TGame.Server.Storage;

internal static class MongoStorage
{
    private const string PingCommand = "ping";
    private const string SuccessField = "ok";
```

`MongoDB.Driver` 提供客户端、数据库接口和认证选项，`MongoDB.Bson` 提供 BSON 文档类型。BSON 是 MongoDB 使用的文档表示；在 C# 中，`BsonDocument` 可以按字段名保存和读取值。

`internal static class` 表示程序集内使用的静态工具，不保存全局连接状态。`private const string` 是编译期字符串常量，仅供这个类使用。`ping` 和 `ok` 是 Mongo 命令及响应字段名称，集中声明能让调用处明确表达其协议含义。

## 2. CreateClient 先检查参数，再组织驱动选项

```csharp
public static MongoClient CreateClient(MongoStorageSettings settings, string password)
{
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentException.ThrowIfNullOrWhiteSpace(password);
    settings.Validate();
```

参数 `MongoStorageSettings` 是纯配置，包含端点、数据库名、认证库、用户名和超时。密码单独传入，正常调用方 [StorageConnections](StorageConnections.cs.md) 根据配置中的变量名从环境读取它。

三个检查按源码顺序执行：

1. `ThrowIfNull(settings)` 拒绝 null 配置，避免下一步在不明确的位置发生空引用错误。
2. `ThrowIfNullOrWhiteSpace(password)` 拒绝 null、空串或全部为空白的密码。
3. `settings.Validate()` 验证配置内容：端点、业务库、认证库、用户名必须有内容，服务器选择超时必须大于零。

这些 `ThrowIf...` 方法正常时没有返回的业务值，发现问题就抛异常。后面的代码只有检查全部通过才会运行。

例如密码读取成功但值是三个空格，`StorageConnections.Password` 的 null 检查不会拒绝它，这里的第二句才会失败。配置存在但端点为空，则会在第三步失败。这两个失败位置表达的是不同问题。

## 3. 对象初始化器逐项赋值

```csharp
var options = new MongoClientSettings
{
    Server = MongoServerAddress.Parse(settings.Endpoint),
    Credential = MongoCredential.CreateCredential(settings.AuthenticationDatabase, settings.UserName, password),
    ServerSelectionTimeout = TimeSpan.FromSeconds(settings.ServerSelectionTimeoutSeconds)
};

return new MongoClient(options);
```

`new MongoClientSettings { ... }` 是对象初始化器：先创建选项对象，再按写出的属性赋值。花括号里的 `Server = ...` 是对象属性赋值，不是在声明新的局部变量。

`var options` 的静态类型由右边推断为 `MongoClientSettings`。`return new MongoClient(options)` 用这些选项创建客户端，再把其引用交给调用者；这个工具类自己不保存引用。

### Server：把配置地址解析成驱动对象

```csharp
Server = MongoServerAddress.Parse(settings.Endpoint),
```

左边是驱动选项，右边是把配置字符串交给驱动解析。例如端点可以是项目隧道映射后的 `127.0.0.1:37017`。这里没有自行拆分主机和端口，也没有替用户开 SSH 隧道。

配置层的“字符串非空”与驱动层的“字符串能解析成地址”是两项不同检查。地址格式不对时，驱动解析异常直接向外传播。

### Credential：认证数据库不等于业务数据库

```csharp
Credential = MongoCredential.CreateCredential(settings.AuthenticationDatabase, settings.UserName, password),
```

这个调用告诉驱动用哪个认证数据库中的账号进行认证。业务数据存放在哪里，要由调用者后续 `GetDatabase(...)` 决定。

概念例：账号在 `admin` 认证，而摘要存在 `profiles` 库中。这时认证数据库是 `admin`，`ProfileStore` 选中的业务库是 `profiles`。两者可以不同，不能因为认证失败就随意把业务库名替换成认证库名。

### ServerSelectionTimeout：只命名它实际控制的等待

```csharp
ServerSelectionTimeout = TimeSpan.FromSeconds(settings.ServerSelectionTimeoutSeconds)
```

配置存的是整数秒数，`TimeSpan.FromSeconds` 把它转换成时长对象，供驱动在选择可用服务器时使用。它不是本项目整个 `OpenAsync` 的统一倒计时，也不能保证 Redis 初始化、后续查询和清理全部在这段时间内结束。

本方法没有发送 `ping`，也没有实际写集合来证明权限。驱动可能有自己的连接管理行为，但这里不能仅靠 `new MongoClient(...)` 返回就宣布数据库已经可用。

## 4. CheckConnectionAsync 的返回类型与实参

```csharp
public static async Task CheckConnectionAsync(IMongoDatabase database, CancellationToken stop)
```

`IMongoDatabase` 是驱动提供的数据库操作接口。调用者先执行 `mongo.GetDatabase(settings.MongoDatabase)` 取得指定库的句柄，再传到这里。接口参数使本方法不必知道客户端是如何创建的。

`CancellationToken stop` 是取消令牌，只能观察取消，不能主动调用它来取消别人。取消请求由持有对应 `CancellationTokenSource` 的上层发出。

`Task` 没有结果类型参数：调用方等待的是“检查结束”，不会得到一个 `bool`。成功时正常返回；失败时通过任务的异常或取消向等待方报告。

`async` 允许方法里写 `await`，不会自动为方法开一条线程。执行先从调用线程开始；在未完成的异步等待处可返回控制权。这里没有 `ConfigureAwait(false)`，续体安排会受调用环境同步上下文影响，不能承诺所有后续代码都在固定后台线程执行。

## 5. 发送命令和等待返回分两步理解

```csharp
var command = new BsonDocument(PingCommand, 1);
var reply = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: stop);
```

第一句在内存里创建文档，含一个字段 `ping`，值为数值 `1`。可把它理解为下面的命令数据：

```json
{ "ping": 1 }
```

第二句调用驱动发送命令，得到异步任务，再等待任务提供响应。`<BsonDocument>` 是泛型类型实参，指定响应结果用 BSON 文档表示。`cancellationToken: stop` 是命名实参，冒号前是参数名，冒号后是当前变量。

只有等待成功，局部变量 `reply` 才会拿到响应。若网络、认证、服务器选择或取消使驱动调用失败，后面的 `if` 不会执行，异常会继续传播。

`stop` 传给驱动表示允许这次操作观察取消；它不是强制杀线程，也不是向 Mongo 数据库发送“撤销此前所有操作”。检查任务取消后，客户端对象仍归调用方管理。

## 6. 为什么还要读取 ok 字段

```csharp
if (reply[SuccessField].ToDouble() != 1)
{
    throw new IOException("MongoDB connection check failed.");
}
```

`reply[SuccessField]` 是索引器访问，按字段名 `ok` 取 BSON 值。`ToDouble()` 把该值转换成 `double`，然后与数值 `1` 比较。

例如响应中 `ok` 是 `1`，比较为假，方法正常结束。若可转换的值是 `0`，进入分支，抛出 `IOException`。这里没有把不成功的检查包装成一个仍可使用的“成功客户端”。

若 `ok` 字段缺失或内容不能转换，索引/转换本身也会失败。源码没有宽泛捕获这些异常，因此不要把该方法理解成“无论什么坏响应都只会抛 IOException”。

一次 `ping` 成功只能说明这次命令完成。集合读取、替换、删除可能遇到自己的权限或运行期故障，不能由 `ping` 推导“以后所有读写都成功”。

## 7. 客户端由谁释放

创建客户端与检查连接都没有在本类中缓存状态。其正常使用顺序来自 `ProfileStore.OpenAsync`：

```text
创建 MongoClient
  → 对业务库等待 CheckConnectionAsync
  → 初始化 Redis
  → 把两个客户端交给新 ProfileStore
```

Mongo 客户端已经创建而后续初始化失败时，调用方 `catch` 会执行 `mongo.Dispose()` 并重新抛出错误。成功打开后，`ProfileStore.Dispose()` 才负责释放存储客户端。

本文件不会释放传入的 `IMongoDatabase`，也不会关闭上层共享的 ID 或日志服务。这里没有另建事务、会话或全局服务容器。

局部验证场景可读 [MongoStorageChecks 详解](../../../tests/engine/MongoStorageChecks.cs.md)：它检查驱动选项，并用不可服务的回环端点观察取消和选择超时。真正操作摘要与缓存的场景在 [ProfileStorageChecks 源码](../../../../../../code/tests/engine/ProfileStorageChecks.cs)。本文是源码阅读，不表示本轮实际连接过 Mongo 或运行过这些检查。
