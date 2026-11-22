# ZooKeeperOptions.cs 代码详解

源码：[code/server/engine/ZooKeeperOptions.cs](../../../../../code/server/engine/ZooKeeperOptions.cs)

## 职责与字段

`ZooKeeperOptions` 是 ZooKeeper 连接和命名空间配置的 record，使用 init 属性供加载时赋值。`Endpoint` 是服务连接串，`DiscoveryRoot` 是进程发现根路径，`WorkerRootPath` 是雪花 worker 占号根路径。`SessionTimeoutMilliseconds` 与 `ConnectTimeoutMilliseconds` 分别控制 ZooKeeper 会话超时及本地等待连接/租约操作的超时预算。

`FileName` 引用统一 `server.yaml`；`SectionName` 为 `zooKeeperOptions`。`Load(path)` 创建完整 `ServerConfiguration`，返回它已校验并持有的 `ZooKeeperOptions`，不只读取一个孤立的 ZooKeeper 片段。宿主直接注册已有配置持有的同一对象。

## 校验边界

`Validate` 要求连接串非空，验证 `DiscoveryRoot`，并要求两个超时值大于零。它不在此解析连接串的主机格式，也不验证 `WorkerRootPath`；worker 路径由 [IdGeneratorOptions.cs](identifiers/IdGeneratorOptions.cs.md) 及实际申请入口验证。

`ValidatePath` 是内部共享规则：路径必须非空、以 `/` 开头且不能以 `/` 结尾，每一段不能是空串、`.` 或 `..`。因此 `/tgame/workers` 合法，`/`、`relative/path`、`/tgame//workers` 不合法。

## 生命周期

配置对象没有网络连接或可释放资源。[ZooKeeperProxy.cs](ZooKeeperProxy.cs.md) 构造时再次验证这些参数，但连接建立要等宿主显式调用 `InitializeAsync`。配置加载与对象构造都不代表 ZooKeeper 已连接。
