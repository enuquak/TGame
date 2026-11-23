# ZooKeeperOptions.cs 代码详解

源码：[code/server/engine/ZooKeeperOptions.cs](../../../../../code/server/engine/ZooKeeperOptions.cs)

## 配置如何进入代理

`ServerConfiguration` 读取 `server.yaml` 及 include 文件，从运维配置的 `zooKeeperOptions` 绑定这份对象。`SectionName` 给出该键，`FileName` 引用统一的入口文件名。独立使用者可以调用 `ZooKeeperOptions.Load(path)`，它创建完整的 `ServerConfiguration` 并返回其中的选项，所以会一起走整份配置的解析和校验。App 注册已有配置持有的同一实例。配置从 YAML 读到内存，没有文件写回接口。见 [ServerConfiguration.cs](configuration/ServerConfiguration.cs.md)、[ServerServices.cs](../app/ServerServices.cs.md)、[ops.yaml](../../../../../code/server/config/ops.yaml)。

| 属性 | 用途 |
| --- | --- |
| `Endpoint` | ZooKeeper 客户端连接串，例如 `127.0.0.1:2181` |
| `DiscoveryRoot` | 进程占号和发布的根，例如 `/tgame/test`，其下可有 `server-Router1` |
| `WorkerRootPath` | ID worker 节点根，例如 `/tgame/id-workers`，其下可有 `0` |
| `SessionTimeoutMilliseconds` | 创建 ZooKeeper 客户端时传入的会话超时参数 |
| `ConnectTimeoutMilliseconds` | 代理初次等待会话、申请 worker 和删除 worker 的本地操作时限 |

`record` 支持值比较和 `with` 复制语法。测试使用 `options with { DiscoveryRoot = ... }` 创建带独立发现根的对象，原对象保留原值。所有选项用 `init` 来限制常规赋值时机。两个超时还有 `required`，普通 C# 构造调用需要提供它们，但具体数值仍要通过校验。见 [ZooKeeperProxyChecks.cs](../../tests/engine/ZooKeeperProxyChecks.cs.md)。

## Validate 与 ValidatePath

`Validate()` 依次要求连接串含非空白字符、发现根合法、两个超时值大于零。连接串的格式解析发生在客户端创建时。worker 根单独交给 `IdGeneratorOptions.Validate` 校验，代理实际申请 worker 时也会复用相同的路径规则。见 [IdGeneratorOptions.cs](identifiers/IdGeneratorOptions.cs.md)、[ZooKeeperProxy.cs](ZooKeeperProxy.cs.md)。

`ValidatePath(path)` 先拒绝空白，要求以 `/` 开头且末尾无 `/`。`path[1..]` 是 C# 范围表达式，表示从下标 1 取到字符串末尾，正好切掉开头的斜杠。接下来按 `/` 分段，每段都要非空，`segment is "." or ".."` 用模式匹配拒绝两个特殊段。例如 `/tgame/test` 合法，`/` 在末尾检查被拒绝，`/tgame//test` 因中间空段被拒绝，`/tgame/../test` 因 `..` 段被拒绝。路径和超时校验都是本地操作，实际 ZooKeeper 连接由代理的 `InitializeAsync` 建立。
