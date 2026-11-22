# IdGeneratorOptions.cs 代码详解

源码：[code/server/engine/identifiers/IdGeneratorOptions.cs](../../../../../../code/server/engine/identifiers/IdGeneratorOptions.cs)

## 职责与配置来源

`IdGeneratorOptions` 只保留 ID 生成器真正需要的配置：必填的 `WorkerRootPath`。该路径是 ZooKeeper 中用于争用 worker 编号的根节点，不是本地目录。

`Load(path)` 创建完整 `ServerConfiguration` 并返回它持有的 `Identifiers`。统一配置加载期间从 `ZooKeeperOptions.WorkerRootPath` 创建并校验这份选项。`FileName` 也引用统一 `server.yaml`。因此配置文件只声明一份 worker 路径，不额外维护 ID 专用配置节。

## 校验与依赖

`Validate` 复用 `ZooKeeperOptions.ValidatePath`，要求绝对 ZooKeeper 路径、无末尾斜杠，也没有空段、`.` 或 `..`。本文件不申请编号，不决定雪花位宽或时间起点。

运行时 [IdGenerator.cs](IdGenerator.cs.md) 接收该对象，[SnowflakeIdGenerator.cs](SnowflakeIdGenerator.cs.md) 再请求共享 ZooKeeper 代理申请租约。静态加载入口供独立调用方显式加载完整配置；宿主直接注册已加载 `ServerConfiguration.Identifiers`，不重新创建相同选项。
