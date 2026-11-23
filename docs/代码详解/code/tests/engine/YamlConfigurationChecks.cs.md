# YamlConfigurationChecks.cs 代码详解

源码：[code/tests/engine/YamlConfigurationChecks.cs](../../../../../code/tests/engine/YamlConfigurationChecks.cs)

## 职责与执行条件

`Run()` 检查统一 YAML 配置入口的展开、别名隔离、严格校验和已加载值的独立性。首先读取构建输出 `config/server.yaml`，确认日志级别为 Debug、数据库为 `tgame_test`，Mongo 与 Redis 用户名一致，Mongo 认证库与数据库一致。这一步依赖随测试输出复制的实际配置及其 YAML 别名。

它关注“怎样读配置”，[ServerConfigurationChecks.cs](ServerConfigurationChecks.cs.md) 关注“读出的拓扑是否合理”。两者都会调用同一个生产 [ServerConfiguration.cs](../../server/engine/configuration/ServerConfiguration.cs.md)，没有为测试另造解析器。

## 输入文件是怎样准备出来的

`Path.GetTempPath()` 取得系统临时目录，`Guid.NewGuid():N` 产生不带连字符的随机名字，用于隔离本次测试。随后创建 `entry`、`first` 和 `second` 三条路径，分别对应入口、第一份 include、第二份 include。

先从有效配置取得独立字典，再替换需要测试的几项：

```csharp
var baseline = settings.Read<Dictionary<string, object>>();
baseline.Remove("mongo");
baseline.Remove("redis");
baseline["include"] = new[] { "first", "second" };
baseline[ServerConfiguration.RunDirectoryKey] = "logs/original";
```

删除 `mongo` 和 `redis` 是因为它们将由 include 提供；如果入口仍保留同名节，加载器会按顶层重名规则拒绝。其余核心字段保留，使“别名隔离”这个场景可以完整通过配置校验。

测试最后使用 `SerializerBuilder` 把字典写成临时 YAML。生产配置对象只负责读取；文件写入是测试准备输入的一部分。

## 包含文件与已加载配置

测试在 GUID 临时目录内创建入口及 `first.yaml`、`second.yaml`。入口以默认完整配置为基础，移除原 Mongo/Redis 节，再让 include 提供这两节，保留核心字段、数量和拓扑以通过统一校验。两份包含文件都使用名为 `shared` 的锚点，但值分别为 alpha 与 beta；读取后要求各自别名保持所属文件的值，证明锚点不会跨文件串用。相对运行目录应以指定基目录解析。

两份文件的关键内容分别是：

```yaml
# first.yaml
mongo:
  endpoint: &shared alpha
  databaseName: *shared
```

```yaml
# second.yaml
redis:
  endpoint: &shared beta
  userName: *shared
```

`&shared` 定义锚点，`*shared` 引用本文件中的锚点。加载后 `Mongo.DatabaseName` 必须为 `alpha`，`Redis.UserName` 必须为 `beta`。测试由此证明每份 YAML 先独立解析和展开别名，再合并顶层字段；不能把两份文本简单拼接成一个共享锚点空间。

这里 Mongo/Redis 数据刻意很简化，但基础配置允许可选存储字段在真正使用时再做存储语义校验。这个测试并没有尝试连接这些 `alpha`、`beta` 地址。

删除两份源包含文件后，已加载的 `ServerConfiguration` 仍保留原配置值；再次加载原入口则因包含文件已删除而抛 `FileNotFoundException`。此检查验证单个进程的配置只加载一次，不生成落盘快照。

这两个动作要连起来读：

```text
首次加载成功 → 删除 include 文件 → 查询旧对象：仍有 beta
                                  → 创建新对象：文件缺失，失败
```

内存中的值已经独立于源文件，但创建新对象仍然依赖源文件。`ResolveRunDirectory(directory)` 的断言另行验证路径基准：结果是传入目录下的 `logs/original`，不是相对于 YAML 文件名猜测目录；此方法也不会创建目录。

## 拒绝的输入

- 两个包含文件重复定义同一顶层键，或入口与包含文件重复定义键。
- `include` 不是序列、名称带父目录、带 `.yaml` 扩展名、同一名称重复出现。
- 顶层或嵌套映射中的重复键、根为列表、同一文件含多个 YAML 文档。
- 包含文件反向引用入口形成 include 循环，跨文件引用另一份文件的锚点，以及自引用的递归别名。

这些场景用 `Expect<T>` 要求对应异常，未抛错或抛错类型不符都会失败；`Check` 判断具体读取结果。

按错误来源分组看，更容易定位对应解析代码：

| 测试输入 | 为什么应拒绝 |
| --- | --- |
| 两个 include 都定义 `shared` 顶层键 | 合并不采用后者覆盖前者 |
| 入口和 include 都定义 `shared` | 入口也没有更高的覆盖优先级 |
| `include: first` | include 必须为序列，即使只有一个文件 |
| `include: [../first]` 或 `[first.yaml]` | 项目约定只接受同目录、无扩展名的文件名 |
| `include: [first, first]` | 重复包含名称无效 |
| 相同映射内重复键、根为列表、多个文档 | 不是一个严格且无歧义的配置映射 |
| server→first→server | include 递归链形成环 |
| 第二个文件引用第一个文件的锚点 | 锚点不跨文件生效 |
| `value: &recursive [*recursive]` | 递归别名不能无限展开 |

循环里的每轮都覆盖入口文件，然后重新调用构造函数。它没有复用上轮已加载的配置，所以验证的是“这份输入能否通过加载”，不是对旧对象执行热更新。

`Expect<InvalidDataException>(() => new ServerConfiguration(entry))` 中的 Lambda 把构造动作交给异常断言函数；指定异常出现才算符合预期。删除 include 的场景特意使用 `FileNotFoundException`，区别于格式不合法。

## 清理与限制

`finally` 递归删除本次创建的独立临时目录。本文件不连接外部服务，不修改仓库配置，也不逐项覆盖全部配置语义；实际解析、核心字段和拓扑校验均由 `ServerConfiguration` 完成，其他检查文件分别补充字段保持与拓扑场景。可选存储的语义校验仍在使用入口按需执行。成功信息只在所有场景执行完成后输出。
