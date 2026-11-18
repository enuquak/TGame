# IdGeneratorOptions.cs 代码详解

源码：[code/server/engine/identifiers/IdGeneratorOptions.cs](../../../../../../code/server/engine/identifiers/IdGeneratorOptions.cs)

## 职责与配置来源

`IdGeneratorOptions` 只保留 ID 生成器真正需要的配置：必填的 `WorkerRootPath`。该路径是 ZooKeeper 中用于争用 worker 编号的根节点，不是本地目录。

`Load(path)` 先调用 `ZooKeeperOptions.Load` 读取统一 YAML 的 `zooKeeper` 节，再将其中的 `WorkerRootPath` 提取到本配置对象，最后校验。`FileName` 也引用统一 `server.yaml`。因此配置文件只声明一份 worker 路径，不额外维护 ID 专用配置节。

## 校验与依赖

`Validate` 复用 `ZooKeeperOptions.ValidatePath`，要求绝对 ZooKeeper 路径、无末尾斜杠，也没有空段、`.` 或 `..`。本文件不申请编号，不决定雪花位宽或时间起点。

运行时 [IdGenerator.cs](IdGenerator.cs.md) 接收该对象，[SnowflakeIdGenerator.cs](SnowflakeIdGenerator.cs.md) 再请求共享 ZooKeeper 代理申请租约。静态加载入口适合单独加载；宿主也可以从已经加载的 `ZooKeeperOptions` 构造相同数据，复用整个进程的统一配置快照。
