# IdGeneratorOptions.cs 代码详解

源码：[code/server/engine/identifiers/IdGeneratorOptions.cs](../../../../../../code/server/engine/identifiers/IdGeneratorOptions.cs)

`IdGeneratorOptions` 只有一个配置值 `WorkerRootPath`。例如配置为 ZooKeeper 路径 `/tgame/id-workers` 时，worker `0` 的临时节点是 `/tgame/id-workers/0`，由代理在申请租约时创建。文件本身只负责传递并校验根路径。

```csharp
public required string WorkerRootPath { get; init; }
```

这里的 `record` 是 C# 支持值比较的类型声明形式。`required` 要求普通 C# 构造调用提供该属性，例如 `new IdGeneratorOptions { WorkerRootPath = "/tgame/id-workers" }`。`init` 让普通调用方在对象初始化时赋值，之后通过 `get` 读取。路径是否合法仍由 `Validate()` 决定。它调用共享的 `ZooKeeperOptions.ValidatePath`，例如 `/tgame/id-workers` 可以通过，`/tgame/../id-workers` 因 `..` 段被拒绝。见 [ZooKeeperOptions.cs](../ZooKeeperOptions.cs.md)。

统一配置加载器读取 `server.yaml` 及其 include，取 `zooKeeperOptions.workerRootPath` 创建并校验 `Identifiers`。公开静态 `Load(path)` 给独立调用方读取指定入口，内部创建完整的 `ServerConfiguration` 并返回它持有的 `Identifiers`，所以整份配置的其他校验也会执行。`FileName` 直接引用统一的文件名常量。生产 App 向容器注册已有的 `configuration.Identifiers`，供 `IdGenerator` 接收。读取方向是从 YAML 到内存。见 [ServerConfiguration.cs](../configuration/ServerConfiguration.cs.md)、[ServerServices.cs](../../app/ServerServices.cs.md)、[IdGenerator.cs](IdGenerator.cs.md)。
