# 全项目手写源码详解

按当前代码整理，共 **76 个源码文件、76 份独立详解**。覆盖 C#、XAML 和手写 Protobuf 定义，包括测试及桌面控制台；排除工具脚本、生成代码、构建产物、第三方工具二进制、项目构建文件和配置数据。当前客户端目录尚无手写源码。

详解与源码目录对应，文件名保留原扩展名，例如 `Program.cs.md`。文档描述当前实现；测试入口说明不代表本次执行过完整测试或外部服务联调。

## 当前 Review 进度与下一步

更新于 2026-11-20（Asia/Shanghai），按 `review-1119` 本轮代码核对。用户明确确认已读完下面两个文件；其他文件即使已讨论过，也不自动标为已读。历史确认见[代码 Review 进度](../代码Review进度.md)。

- [x] [Program.cs](code/server/app/Program.cs.md)：宿主启动、监督任务、发布服务地址与退出。
- [x] [ServerNode.Discovery.cs](code/server/engine/ServerNode.Discovery.cs.md)：发现快照、连接任务增删、替换与清理。

**下一步从 `ServerNode.cs` 开始，再接 `PeerLink.cs`，把已经读过的启动和发现主线接到实际通信。** 以下按阅读顺序排列；表中链接进入详解，每份详解顶部可跳转源码。

| 顺序 | 阅读文件 | 建议方法顺序与需要弄清的问题 |
| --- | --- | --- |
| 1 | [ServerNode.cs](code/server/engine/ServerNode.cs.md)，对照 [LinkService.cs](code/server/engine/LinkService.cs.md)、[TransportProtocol.cs](code/server/engine/TransportProtocol.cs.md) 和 [transport.proto](code/server/engine/protocols/transport.proto.md) | `MaintainLinkAsync` → `ConnectAsync` / `AcceptConnectAsync` → `RunLinkAsync` → `TryCompleteFirstLinks` → `RunTransportAsync` / `ReceiveFrame` → `CallPeerAsync`。入口校验在哪里？Hello 何时完成？为什么首连完成后断线不撤销 Ready？ |
| 2 | [PeerLink.cs](code/server/engine/PeerLink.cs.md) | `CallAsync<TReply>` → 内部 `CallAsync` → `WriteAsync` → `Receive` → `ReadReply` → `Close`。区分发起方的 `waiting` 与处理方的请求队列；跟踪请求 ID、写锁、超时、取消和断线。 |
| 3 | [ZooKeeperProxy.cs](code/server/engine/ZooKeeperProxy.cs.md)、[ZooKeeperSession.cs](code/server/engine/ZooKeeperSession.cs.md)，对照 [DiscoveredProcess.cs](code/server/engine/DiscoveredProcess.cs.md)、[ZooKeeperOptions.cs](code/server/engine/ZooKeeperOptions.cs.md) | `InitializeAsync` → `ClaimProcessAsync` → `RegisterProcessAsync` → `WatchProcessesAsync` → `IsProcessClaimedAsync` → 关闭。空地址占号与已发布地址有什么区别？为什么 `Generation` 能识别同名重启？会话失效为什么要退出？ |
| 4 | [ServerServices.cs](code/server/app/ServerServices.cs.md)、[ServerConfiguration.cs](code/server/engine/configuration/ServerConfiguration.cs.md)、[ServerGroup.cs](code/server/engine/configuration/ServerGroup.cs.md)、[ProcessType.cs](code/server/engine/configuration/ProcessType.cs.md) | 回看 Program 的依赖来源：单例注册与延迟身份解析 → YAML/include 加载与严格校验 → `Dials` / `GetDialTargets` → 首次连接数量 → 实例命名。配合 `code/server/config/` 的三个 YAML 文件阅读。 |
| 5 | [IdGenerator.cs](code/server/engine/identifiers/IdGenerator.cs.md)、[IdGeneratorOptions.cs](code/server/engine/identifiers/IdGeneratorOptions.cs.md)、[IdText.cs](code/server/engine/identifiers/IdText.cs.md)，再看 [SnowflakeIdGenerator.cs](code/server/engine/identifiers/SnowflakeIdGenerator.cs.md)、[SnowflakeSequence.cs](code/server/engine/identifiers/SnowflakeSequence.cs.md) | 先看初始化、worker 租约、`GenId` 与释放，再看位布局和时钟边界。区分进程实例名、连接 ID 和请求 ID。回到 ZooKeeperProxy 的 `AcquireWorkerAsync` 补齐租约来源。 |
| 6 | [GameLog.cs](code/server/engine/GameLog.cs.md)、[ServerLogProtocol.cs](code/server/engine/logging/ServerLogProtocol.cs.md)、[ServerLogLevel.cs](code/server/engine/logging/ServerLogLevel.cs.md)、[LogEventJson.cs](code/server/engine/logging/LogEventJson.cs.md)，对照 [ServerRuntime.cs](code/server/engine/configuration/ServerRuntime.cs.md)、[ServerRunFiles.cs](code/server/engine/configuration/ServerRunFiles.cs.md)、[EngineEnvironment.cs](code/server/engine/configuration/EngineEnvironment.cs.md) | 日志何时创建和释放？`LINK_UP`、`LINKS_READY`、`PROCESS_READY` 各表示什么？PID 加启动时间如何避免误停新进程？ |
| 7 | [StartupReadinessChecks.cs](code/tests/engine/StartupReadinessChecks.cs.md) → [NodeCommunicationChecks.cs](code/tests/engine/NodeCommunicationChecks.cs.md) → [RpcChecks.cs](code/tests/engine/RpcChecks.cs.md) → [DynamicTopologyChecks.cs](code/tests/engine/DynamicTopologyChecks.cs.md) → [DiscoveryLinkChecks.cs](code/tests/engine/DiscoveryLinkChecks.cs.md) | 用断言反查前面理解。先读准备、动作和结果，不需要执行测试；按需回查 [FrameStream.cs](code/tests/engine/FrameStream.cs.md)、[TestNodes.cs](code/tests/engine/TestNodes.cs.md)、[TestServices.cs](code/tests/engine/TestServices.cs.md) 及[测试入口](code/tests/engine/Program.cs.md)。 |
| 8（按需） | [ConsoleSession.cs](tools/server-console/ConsoleSession.cs.md) → [ServerInstance.cs](tools/server-console/ServerInstance.cs.md) → [RollingLog.cs](tools/server-console/RollingLog.cs.md) → [MainWindow.xaml.cs](tools/server-console/MainWindow.xaml.cs.md) | 看控制台如何准备快照、起停进程、恢复身份、回放日志和刷新页面；其余界面及控制台测试从下方索引进入。 |
| 9（按需） | [StorageConnections.cs](code/server/logic/storage/StorageConnections.cs.md)、[MongoStorage.cs](code/server/logic/storage/MongoStorage.cs.md)、[ProfileStore.cs](code/server/logic/storage/ProfileStore.cs.md)、[profile.proto](code/shared/protocols/profile.proto.md) | 独立存储与业务共享协议不在当前进程启动及基础 RPC 主线中，放在框架读通之后。 |

每读完一个文件，再更新阅读确认。已读的两个文件无需重头复读，只在追踪调用时回看；更完整的问题清单与检查入口见[代码 Review 路径](../代码Review路径.md)。

## 服务器宿主（2）

| 源码 | 详解 |
| --- | --- |
| [code/server/app/Program.cs](../../code/server/app/Program.cs) | [阅读详解](code/server/app/Program.cs.md) |
| [code/server/app/ServerServices.cs](../../code/server/app/ServerServices.cs) | [阅读详解](code/server/app/ServerServices.cs.md) |

## 服务器引擎（27）

| 源码 | 详解 |
| --- | --- |
| [code/server/engine/DiscoveredProcess.cs](../../code/server/engine/DiscoveredProcess.cs) | [阅读详解](code/server/engine/DiscoveredProcess.cs.md) |
| [code/server/engine/GameLog.cs](../../code/server/engine/GameLog.cs) | [阅读详解](code/server/engine/GameLog.cs.md) |
| [code/server/engine/LinkService.cs](../../code/server/engine/LinkService.cs) | [阅读详解](code/server/engine/LinkService.cs.md) |
| [code/server/engine/PeerLink.cs](../../code/server/engine/PeerLink.cs) | [阅读详解](code/server/engine/PeerLink.cs.md) |
| [code/server/engine/ServerNode.Discovery.cs](../../code/server/engine/ServerNode.Discovery.cs) | [阅读详解](code/server/engine/ServerNode.Discovery.cs.md) |
| [code/server/engine/ServerNode.cs](../../code/server/engine/ServerNode.cs) | [阅读详解](code/server/engine/ServerNode.cs.md) |
| [code/server/engine/TransportProtocol.cs](../../code/server/engine/TransportProtocol.cs) | [阅读详解](code/server/engine/TransportProtocol.cs.md) |
| [code/server/engine/ZooKeeperOptions.cs](../../code/server/engine/ZooKeeperOptions.cs) | [阅读详解](code/server/engine/ZooKeeperOptions.cs.md) |
| [code/server/engine/ZooKeeperProxy.cs](../../code/server/engine/ZooKeeperProxy.cs) | [阅读详解](code/server/engine/ZooKeeperProxy.cs.md) |
| [code/server/engine/ZooKeeperSession.cs](../../code/server/engine/ZooKeeperSession.cs) | [阅读详解](code/server/engine/ZooKeeperSession.cs.md) |
| [code/server/engine/configuration/EngineEnvironment.cs](../../code/server/engine/configuration/EngineEnvironment.cs) | [阅读详解](code/server/engine/configuration/EngineEnvironment.cs.md) |
| [code/server/engine/configuration/MongoStorageSettings.cs](../../code/server/engine/configuration/MongoStorageSettings.cs) | [阅读详解](code/server/engine/configuration/MongoStorageSettings.cs.md) |
| [code/server/engine/configuration/ProcessType.cs](../../code/server/engine/configuration/ProcessType.cs) | [阅读详解](code/server/engine/configuration/ProcessType.cs.md) |
| [code/server/engine/configuration/RedisStorageSettings.cs](../../code/server/engine/configuration/RedisStorageSettings.cs) | [阅读详解](code/server/engine/configuration/RedisStorageSettings.cs.md) |
| [code/server/engine/configuration/ServerConfiguration.cs](../../code/server/engine/configuration/ServerConfiguration.cs) | [阅读详解](code/server/engine/configuration/ServerConfiguration.cs.md) |
| [code/server/engine/configuration/ServerGroup.cs](../../code/server/engine/configuration/ServerGroup.cs) | [阅读详解](code/server/engine/configuration/ServerGroup.cs.md) |
| [code/server/engine/configuration/ServerRunFiles.cs](../../code/server/engine/configuration/ServerRunFiles.cs) | [阅读详解](code/server/engine/configuration/ServerRunFiles.cs.md) |
| [code/server/engine/configuration/ServerRuntime.cs](../../code/server/engine/configuration/ServerRuntime.cs) | [阅读详解](code/server/engine/configuration/ServerRuntime.cs.md) |
| [code/server/engine/identifiers/IdGenerator.cs](../../code/server/engine/identifiers/IdGenerator.cs) | [阅读详解](code/server/engine/identifiers/IdGenerator.cs.md) |
| [code/server/engine/identifiers/IdGeneratorOptions.cs](../../code/server/engine/identifiers/IdGeneratorOptions.cs) | [阅读详解](code/server/engine/identifiers/IdGeneratorOptions.cs.md) |
| [code/server/engine/identifiers/IdText.cs](../../code/server/engine/identifiers/IdText.cs) | [阅读详解](code/server/engine/identifiers/IdText.cs.md) |
| [code/server/engine/identifiers/SnowflakeIdGenerator.cs](../../code/server/engine/identifiers/SnowflakeIdGenerator.cs) | [阅读详解](code/server/engine/identifiers/SnowflakeIdGenerator.cs.md) |
| [code/server/engine/identifiers/SnowflakeSequence.cs](../../code/server/engine/identifiers/SnowflakeSequence.cs) | [阅读详解](code/server/engine/identifiers/SnowflakeSequence.cs.md) |
| [code/server/engine/logging/LogEventJson.cs](../../code/server/engine/logging/LogEventJson.cs) | [阅读详解](code/server/engine/logging/LogEventJson.cs.md) |
| [code/server/engine/logging/ServerLogLevel.cs](../../code/server/engine/logging/ServerLogLevel.cs) | [阅读详解](code/server/engine/logging/ServerLogLevel.cs.md) |
| [code/server/engine/logging/ServerLogProtocol.cs](../../code/server/engine/logging/ServerLogProtocol.cs) | [阅读详解](code/server/engine/logging/ServerLogProtocol.cs.md) |
| [code/server/engine/protocols/transport.proto](../../code/server/engine/protocols/transport.proto) | [阅读详解](code/server/engine/protocols/transport.proto.md) |

## 独立存储逻辑（3）

| 源码 | 详解 |
| --- | --- |
| [code/server/logic/storage/MongoStorage.cs](../../code/server/logic/storage/MongoStorage.cs) | [阅读详解](code/server/logic/storage/MongoStorage.cs.md) |
| [code/server/logic/storage/ProfileStore.cs](../../code/server/logic/storage/ProfileStore.cs) | [阅读详解](code/server/logic/storage/ProfileStore.cs.md) |
| [code/server/logic/storage/StorageConnections.cs](../../code/server/logic/storage/StorageConnections.cs) | [阅读详解](code/server/logic/storage/StorageConnections.cs.md) |

## 共享代码与业务协议（3）

| 源码 | 详解 |
| --- | --- |
| [code/shared/csharp/ProfileLogEvents.cs](../../code/shared/csharp/ProfileLogEvents.cs) | [阅读详解](code/shared/csharp/ProfileLogEvents.cs.md) |
| [code/shared/csharp/ServerEnvironment.cs](../../code/shared/csharp/ServerEnvironment.cs) | [阅读详解](code/shared/csharp/ServerEnvironment.cs.md) |
| [code/shared/protocols/profile.proto](../../code/shared/protocols/profile.proto) | [阅读详解](code/shared/protocols/profile.proto.md) |

## 引擎及存储测试（22）

| 源码 | 详解 |
| --- | --- |
| [code/tests/engine/DiscoveryLinkChecks.cs](../../code/tests/engine/DiscoveryLinkChecks.cs) | [阅读详解](code/tests/engine/DiscoveryLinkChecks.cs.md) |
| [code/tests/engine/DynamicTopologyChecks.cs](../../code/tests/engine/DynamicTopologyChecks.cs) | [阅读详解](code/tests/engine/DynamicTopologyChecks.cs.md) |
| [code/tests/engine/FrameStream.cs](../../code/tests/engine/FrameStream.cs) | [阅读详解](code/tests/engine/FrameStream.cs.md) |
| [code/tests/engine/GroupChecks.cs](../../code/tests/engine/GroupChecks.cs) | [阅读详解](code/tests/engine/GroupChecks.cs.md) |
| [code/tests/engine/IdGeneratorChecks.cs](../../code/tests/engine/IdGeneratorChecks.cs) | [阅读详解](code/tests/engine/IdGeneratorChecks.cs.md) |
| [code/tests/engine/IdGeneratorLifecycleChecks.cs](../../code/tests/engine/IdGeneratorLifecycleChecks.cs) | [阅读详解](code/tests/engine/IdGeneratorLifecycleChecks.cs.md) |
| [code/tests/engine/IocChecks.cs](../../code/tests/engine/IocChecks.cs) | [阅读详解](code/tests/engine/IocChecks.cs.md) |
| [code/tests/engine/LogLevelChecks.cs](../../code/tests/engine/LogLevelChecks.cs) | [阅读详解](code/tests/engine/LogLevelChecks.cs.md) |
| [code/tests/engine/MongoStorageChecks.cs](../../code/tests/engine/MongoStorageChecks.cs) | [阅读详解](code/tests/engine/MongoStorageChecks.cs.md) |
| [code/tests/engine/NodeCommunicationChecks.cs](../../code/tests/engine/NodeCommunicationChecks.cs) | [阅读详解](code/tests/engine/NodeCommunicationChecks.cs.md) |
| [code/tests/engine/ProfileStorageChecks.cs](../../code/tests/engine/ProfileStorageChecks.cs) | [阅读详解](code/tests/engine/ProfileStorageChecks.cs.md) |
| [code/tests/engine/Program.cs](../../code/tests/engine/Program.cs) | [阅读详解](code/tests/engine/Program.cs.md) |
| [code/tests/engine/RpcChecks.cs](../../code/tests/engine/RpcChecks.cs) | [阅读详解](code/tests/engine/RpcChecks.cs.md) |
| [code/tests/engine/RpcFrames.cs](../../code/tests/engine/RpcFrames.cs) | [阅读详解](code/tests/engine/RpcFrames.cs.md) |
| [code/tests/engine/ServerConfigurationChecks.cs](../../code/tests/engine/ServerConfigurationChecks.cs) | [阅读详解](code/tests/engine/ServerConfigurationChecks.cs.md) |
| [code/tests/engine/SettingsChecks.cs](../../code/tests/engine/SettingsChecks.cs) | [阅读详解](code/tests/engine/SettingsChecks.cs.md) |
| [code/tests/engine/StartupPublicationChecks.cs](../../code/tests/engine/StartupPublicationChecks.cs) | [阅读详解](code/tests/engine/StartupPublicationChecks.cs.md) |
| [code/tests/engine/StartupReadinessChecks.cs](../../code/tests/engine/StartupReadinessChecks.cs) | [阅读详解](code/tests/engine/StartupReadinessChecks.cs.md) |
| [code/tests/engine/TestNodes.cs](../../code/tests/engine/TestNodes.cs) | [阅读详解](code/tests/engine/TestNodes.cs.md) |
| [code/tests/engine/TestServices.cs](../../code/tests/engine/TestServices.cs) | [阅读详解](code/tests/engine/TestServices.cs.md) |
| [code/tests/engine/YamlConfigurationChecks.cs](../../code/tests/engine/YamlConfigurationChecks.cs) | [阅读详解](code/tests/engine/YamlConfigurationChecks.cs.md) |
| [code/tests/engine/ZooKeeperProxyChecks.cs](../../code/tests/engine/ZooKeeperProxyChecks.cs) | [阅读详解](code/tests/engine/ZooKeeperProxyChecks.cs.md) |

## 桌面控制台及测试（19）

| 源码 | 详解 |
| --- | --- |
| [tools/server-console/ConsoleProject.cs](../../tools/server-console/ConsoleProject.cs) | [阅读详解](tools/server-console/ConsoleProject.cs.md) |
| [tools/server-console/ConsoleSession.cs](../../tools/server-console/ConsoleSession.cs) | [阅读详解](tools/server-console/ConsoleSession.cs.md) |
| [tools/server-console/ConsoleTheme.xaml](../../tools/server-console/ConsoleTheme.xaml) | [阅读详解](tools/server-console/ConsoleTheme.xaml.md) |
| [tools/server-console/LogEntry.cs](../../tools/server-console/LogEntry.cs) | [阅读详解](tools/server-console/LogEntry.cs.md) |
| [tools/server-console/MainWindow.Logs.cs](../../tools/server-console/MainWindow.Logs.cs) | [阅读详解](tools/server-console/MainWindow.Logs.cs.md) |
| [tools/server-console/MainWindow.xaml](../../tools/server-console/MainWindow.xaml) | [阅读详解](tools/server-console/MainWindow.xaml.md) |
| [tools/server-console/MainWindow.xaml.cs](../../tools/server-console/MainWindow.xaml.cs) | [阅读详解](tools/server-console/MainWindow.xaml.cs.md) |
| [tools/server-console/ProcessPage.cs](../../tools/server-console/ProcessPage.cs) | [阅读详解](tools/server-console/ProcessPage.cs.md) |
| [tools/server-console/Program.cs](../../tools/server-console/Program.cs) | [阅读详解](tools/server-console/Program.cs.md) |
| [tools/server-console/RollingLog.cs](../../tools/server-console/RollingLog.cs) | [阅读详解](tools/server-console/RollingLog.cs.md) |
| [tools/server-console/ServerInstance.cs](../../tools/server-console/ServerInstance.cs) | [阅读详解](tools/server-console/ServerInstance.cs.md) |
| [tools/server-console/StopServersWindow.xaml](../../tools/server-console/StopServersWindow.xaml) | [阅读详解](tools/server-console/StopServersWindow.xaml.md) |
| [tools/server-console/StopServersWindow.xaml.cs](../../tools/server-console/StopServersWindow.xaml.cs) | [阅读详解](tools/server-console/StopServersWindow.xaml.cs.md) |
| [tools/server-console/UserSettings.cs](../../tools/server-console/UserSettings.cs) | [阅读详解](tools/server-console/UserSettings.cs.md) |
| [tools/server-console/tests/ConfigChecks.cs](../../tools/server-console/tests/ConfigChecks.cs) | [阅读详解](tools/server-console/tests/ConfigChecks.cs.md) |
| [tools/server-console/tests/DynamicIdentityChecks.cs](../../tools/server-console/tests/DynamicIdentityChecks.cs) | [阅读详解](tools/server-console/tests/DynamicIdentityChecks.cs.md) |
| [tools/server-console/tests/GroupConfigChecks.cs](../../tools/server-console/tests/GroupConfigChecks.cs) | [阅读详解](tools/server-console/tests/GroupConfigChecks.cs.md) |
| [tools/server-console/tests/LogChecks.cs](../../tools/server-console/tests/LogChecks.cs) | [阅读详解](tools/server-console/tests/LogChecks.cs.md) |
| [tools/server-console/tests/Program.cs](../../tools/server-console/tests/Program.cs) | [阅读详解](tools/server-console/tests/Program.cs.md) |
