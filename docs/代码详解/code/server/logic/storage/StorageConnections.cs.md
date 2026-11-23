# StorageConnections.cs 代码详解

[源码](../../../../../../code/server/logic/storage/StorageConnections.cs)

## 谁调用它

独立存储工具已经拿到 `ServerConfiguration`，其中有数据库地址、库名、用户名和密码环境变量名。 `StorageConnections` 把这些配置与运行环境中的密码合在一起，交给 MongoDB 驱动。当前调用者 `ProfileStore` 用 `Mongo(settings)` 创建 Mongo 客户端，也用 `Password(settings.RedisPasswordVariable)` 读取 Redis 密码。 [ProfileStore 详解](ProfileStore.cs.md)

`internal static class` 表示程序集内部的静态工具类。调用者直接写 `StorageConnections.Mongo(settings)`，无需构造对象。类里没有字段，不保存客户端、配置或密码，也没有跨调用复用连接的逻辑。每次调用 `Mongo` 都会创建一个新客户端。

## `Mongo(settings)` 如何组装参数

```csharp
var options = settings.Mongo ?? throw new InvalidDataException("Missing MongoDB storage configuration.");
return MongoStorage.CreateClient(options, Password(settings.MongoPasswordVariable));
```

1. `settings.Mongo` 是同一个配置实例上的可选 Mongo 节点。空合并运算符 `??` 在左边有值时直接取左边，左边为 `null` 时才执行右边。因此缺少节点会立即抛 `InvalidDataException`，不会继续读取密码。
2. `settings.MongoPasswordVariable` 从该节点取 Profile 专用的环境变量名。假设它的值为 `GAME_DB_SECRET`，`Password` 查找的是名叫 `GAME_DB_SECRET` 的环境变量，返回的是变量的内容。
3. `MongoStorage.CreateClient` 接收完整的配置对象和密码。端点、用户名、认证数据库和服务器选择超时都来自已有配置。 `StorageConnections` 不再解析 YAML，也不写回文件。

## `Password(key)` 的边界

```csharp
public static string Password(string key) => Environment.GetEnvironmentVariable(key)
    ?? throw new InvalidOperationException($"Missing credential {key}.");
```

`=>` 将方法体写成一个表达式，结果就是返回值。变量缺失时，异常消息只包含变量名。这个方法只判断返回值是否为 `null`，不检查空白，也不修剪或转换密码。Mongo 路径下一步由 `CreateClient` 拒绝空白，Redis 路径把返回值提交给驱动配置。独立 Profile 工具在读取环境变量之前先调用 `ValidateProfileStorage`，校验所需配置节点和变量名。

两个方法都是同步的。调用成功只返回客户端或字符串。Profile 的 Mongo 连通性检查发生在下一步 `MongoStorage.CheckConnectionAsync`。创建成功的客户端交给调用者持有，由 `ProfileStore.OpenAsync` 在中途失败时清理，或由成功返回的 `ProfileStore` 在释放时清理。

[MongoStorage 详解](MongoStorage.cs.md)
