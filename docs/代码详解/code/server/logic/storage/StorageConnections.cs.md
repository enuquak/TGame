# StorageConnections.cs 代码详解

[源码](../../../../../../code/server/logic/storage/StorageConnections.cs)

这份文件把“已经加载的配置”和“当前进程环境里的密码”合在一起，交给 MongoDB 客户端工厂。它只有两个方法，但串起了配置、凭据和客户端所有权三个边界。先读清它，后面 [MongoStorage](MongoStorage.cs.md) 和 [ProfileStore](ProfileStore.cs.md) 的初始化过程就容易跟上。

当前服务器 App 只启动进程基础框架，不在启动阶段初始化 DBMgr/Profile 业务。这里的调用关系来自保留的独立存储实现，不能把它画成当前每个服务器进程必经的启动步骤。

## 1. 命名空间、工具类和服务实例的区别

```csharp
using MongoDB.Driver;
using TGame.Server;
using TGame.Server.Storage;

namespace TGame.Server.Logic;

internal static class StorageConnections
```

文件顶部的 `using` 引入命名空间，使后面能直接写 `MongoClient`、`MongoStorage` 等短名。它不会创建连接，也不是用于自动释放资源的 `using var`。

`namespace ...;` 是文件范围命名空间，后面的类型归在 `TGame.Server.Logic` 下。磁盘目录 `storage/` 不会自动决定命名空间，真正生效的是这条声明。

`internal` 限定类型主要供当前程序集内部访问。`static class` 不能用 `new StorageConnections()` 实例化，调用形式是 `StorageConnections.Mongo(settings)`。这符合这里的用途：它没有字段，只根据实参和环境创建结果，没有保存全局 Mongo 连接或全局配置。

这与项目禁止的静态服务入口有所区别。这里是无状态组装工具；真正跨请求使用的客户端保存到某个 `ProfileStore` 实例里，调用方持有并管理那个实例。不能据此把 `IdGenerator`、`GameLog` 等进程级服务也改成静态全局对象。

## 2. Mongo 方法的签名说了什么

```csharp
public static MongoClient Mongo(ServerConfiguration settings)
{
    var options = settings.Mongo ?? throw new InvalidDataException("Missing MongoDB storage configuration.");
    return MongoStorage.CreateClient(options, Password(settings.MongoPasswordVariable));
}
```

返回类型 `MongoClient` 表示成功时直接拿到驱动客户端。这里没有 `async`、`Task` 或 `await`，这是一个同步方法。同步返回客户端不等于已经验证数据库可访问；真正发送 `ping` 的步骤在 `MongoStorage.CheckConnectionAsync`。

参数 `ServerConfiguration settings` 是调用者已经构造好的配置对象。它包含从 `server.yaml` 及 include 文件加载的内存数据。本方法不会重新读 YAML、自动热重载或写回配置。

`var options` 仍然是有明确编译期类型的局部变量。编译器根据右边的 `settings.Mongo` 推导出 Mongo 配置类型；`var` 不是 JavaScript 那样允许变量之后随意换成不同类型。

### 先检查 Mongo 节点

```csharp
var options = settings.Mongo ?? throw new InvalidDataException("Missing MongoDB storage configuration.");
```

`??` 是空合并运算符。先算左边：

- `settings.Mongo` 非空，表达式结果就是那个配置对象的引用。
- `settings.Mongo` 为 `null`，执行右边的 `throw`，方法立即失败。

因此，缺少 Mongo 节点时不会继续读取密码，也不会创建客户端。`throw` 在这里是表达式形式，能放在 `??` 的右边。

这一句检查的是 `Mongo` 子配置，不是 `settings` 参数本身。如果调用方传入 `null` 配置对象，读取 `settings.Mongo` 时已经失败。本文件没有另一层 `settings == null` 校验。

### 再读环境变量，最后创建客户端

```csharp
return MongoStorage.CreateClient(options, Password(settings.MongoPasswordVariable));
```

调用 `CreateClient` 前，必须先算出它的两个实参：已有的 `options` 引用，以及 `Password(...)` 返回的字符串。可以用下面的概念展开理解执行顺序；这不是源码里的额外方法：

```csharp
string variableName = settings.MongoPasswordVariable;
string password = Password(variableName);
MongoClient client = MongoStorage.CreateClient(options, password);
return client;
```

`MongoPasswordVariable` 从统一配置的 Mongo 节点取得 Profile 使用的密码变量名。它不是密码内容。假设配置里保存的是 `GAME_DB_SECRET`，就会读取名为 `GAME_DB_SECRET` 的环境变量，再把该变量内容作为密码交给驱动工厂。

每次执行这段方法，都会调用一次 `CreateClient`。本类没有缓存客户端；复用发生在成功打开后的 `ProfileStore` 字段中。也不能把静态方法误解为“自动只创建一次”。

## 3. Password 用表达式体返回结果

```csharp
public static string Password(string key) => Environment.GetEnvironmentVariable(key)
    ?? throw new InvalidOperationException($"Missing credential {key}.");
```

方法声明后的 `=>` 表示表达式体。这个方法只需要计算一个值并返回，可以省去外层花括号和 `return`。它与 lambda 都用 `=>` 字符，但语法位置不同：这里左边是完整方法签名，不是在创建一个函数对象。

`Environment.GetEnvironmentVariable(key)` 读取当前进程可见的环境变量。参数 `key` 是变量名；返回字符串是变量内容。这里没有执行 shell 命令，也不会把字符串中的字符当作代码。

同样用 `?? throw` 判断缺失：只有返回值为 `null` 才执行异常表达式。`$"...{key}..."` 是字符串插值，异常文本中填入变量名，源码没有在这条错误消息中输出密码值。

注意这三个输入不能混为一谈：

| 环境读取结果 | Password 的处理 | Mongo 后续的处理 |
| --- | --- | --- |
| `null` | 抛 `InvalidOperationException` | 不会进入客户端工厂 |
| 空字符串或全空白字符串 | 本方法没有单独拒绝非 null 值 | `CreateClient` 的 `ThrowIfNullOrWhiteSpace` 拒绝 |
| 普通字符串 | 原样返回 | 传给驱动认证配置 |

这个方法不做 `Trim()`。密码中的空格可能是值的一部分，不能用“清理用户输入”的习惯随意改变凭据。

若环境变量名本身无效，底层环境 API 也可能拒绝。本方法没有把所有错误统一改写为“密码缺失”。正常 Profile 路径会更早调用 `settings.ValidateProfileStorage()`，检查所需配置和凭据变量名。

## 4. 同一辅助方法也服务于 Redis

[ProfileStore.OpenAsync](ProfileStore.cs.md) 中另一处调用是：

```csharp
Password = StorageConnections.Password(settings.RedisPasswordVariable),
```

左边 `Password` 是 Redis 配置对象的属性名，右边 `StorageConnections.Password(...)` 是本文件的方法调用。两个名字相同不表示递归：赋值对象和方法所属类型不同。

Mongo 路径会把读取结果再交给 `MongoStorage.CreateClient` 校验。Redis 路径直接把结果放进 `ConfigurationOptions.Password`。所以不能说“Password 方法本身已经保证所有调用路径的密码非空白”；它只负责读取及 null 检查。

这一层没有接收 `CancellationToken`。读取环境变量和创建客户端不是这里组织的可取消异步操作。后面 Mongo 的连接检查、查找、替换和删除才把调用方令牌传给驱动。

## 5. 异常和资源最后交给谁

本文件没有 `try/catch`。缺配置、缺密码、配置校验或客户端构造失败都会向调用者传播，不返回 `null` 假装创建成功。

正常路径是：

```text
独立存储使用方准备 ServerConfiguration、IdGenerator、GameLog
  → ProfileStore.OpenAsync 校验存储配置
  → StorageConnections.Mongo 取 Mongo 节点和密码
  → MongoStorage.CreateClient 返回客户端
  → ProfileStore 检查 Mongo、初始化 Redis
  → 成功的 ProfileStore 持有客户端，后续反复使用
```

`StorageConnections` 没有保存刚返回的对象，因此也没有 `Dispose` 可调用。客户端返回后，由接收它的调用方负责生命周期：`ProfileStore.OpenAsync` 中后续初始化失败时会清理 Mongo；正常打开后由 `ProfileStore.Dispose()` 清理。

`ServerConfiguration`、日志和 ID 服务仍由上层提供。创建一个存储客户端不应顺便释放这些共享依赖，也不应建立另一个全局 IoC 容器。

阅读本文件时可以沿两个具体问题回查：端点和认证参数如何变为驱动选项，见 [MongoStorage 详解](MongoStorage.cs.md)；连接建立后如何查询、写入以及处理缓存失败，见 [ProfileStore 详解](ProfileStore.cs.md)。
