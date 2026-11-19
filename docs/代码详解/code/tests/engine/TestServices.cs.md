# TestServices.cs 代码详解

源码：[code/tests/engine/TestServices.cs](../../../../../code/tests/engine/TestServices.cs)

## 职责与依赖组装

`TestServices` 为本地检查提供一组显式共享依赖：`GameLog`、`IdGenerator`、`ZooKeeperProxy` 和 `ServerGroup`。它没有全局静态服务实例；各测试收到同一个对象，按构造函数或方法参数获取依赖。

`Ids` 使用可注入工厂创建 worker 为 `0`、时钟为 `TimeProvider.System` 的 `SnowflakeIdGenerator`。这避免本地检查为了生成合法 ID 而分配真实 ZooKeeper worker；构造 `TestServices` 本身不会初始化 `Ids`，调用方仍须执行初始化。`Startup` 为枚举中的每个进程类型提供首次连接门槛 `1`，适合普通本地链路检查；门槛的特殊场景会另建配置。

## 关键方法

- 构造函数创建 `ZooKeeperProxy`，但不调用 `InitializeAsync`，因此本地检查不会连接 ZooKeeper。
- `LocalZooKeeperOptions()` 返回 `127.0.0.1:1`、`/local-checks` 和一秒的会话/连接超时。这是测试占位配置，不是开发服务的连接地址。
- `CreateNode(process, configuration)` 将传入身份、连接配置及共享测试依赖注入 `ServerNode`。
- `DisposeAsync()` 先释放 ID，再释放 ZooKeeper，最后释放日志，使租约依赖先于会话结束，日志在依赖释放期间仍可用。

## 清理责任与限制

测试代码需要先取消并等待自己启动的链路任务，随后才释放本对象。此处仅管理三个服务的释放，不保存或清理创建过的节点和链路。它模拟进程依赖组合，但没有 App 的容器组装、真实注册、监听或服务发布；本地通过不表示真实基础服务可达。
