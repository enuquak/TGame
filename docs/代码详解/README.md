# 全项目手写源码详解

按当前代码整理，共 **75 个源码文件、75 份独立详解**。覆盖 C#、XAML 和手写 Protobuf 定义，包括测试及桌面控制台；排除工具脚本、生成代码、构建产物、第三方工具二进制、项目构建文件和配置数据。当前客户端目录尚无手写源码。

详解与源码目录对应，文件名保留原扩展名，例如 `Program.cs.md`。文档描述当前实现；测试入口说明不代表本次执行过完整测试或外部服务联调。

本轮按未读计划扩写 ID、ZooKeeper 会话与租约、日志、辅助类型、配套测试及可选控制台/存储入口。阅读时先看职责和字段，再跟随方法的执行顺序，对照短代码片段、具体场景与失败清理；测试文档区分准备、动作、断言和未覆盖的范围。详解变详细不代表文件已经读完，阅读状态仍以用户确认为准。

用户于 2026-11-25 明确了详解深度：面向有其他语言基础的 C# 初学者，结合代码逐段解释语法、执行时机、状态变化和设计原因，不能用概览代替理解过程。具体要求见[阅读方式](../代码Review进度.md#阅读方式)。[IdGeneratorLifecycleChecks.cs](code/tests/engine/IdGeneratorLifecycleChecks.cs.md) 已按会话中的完整讲解重写，可作为深度参照；其余文档尚不能一概视为达到此标准。

## 当前 Review 进度与下一步

更新于 2026-11-24（Asia/Shanghai），按 `review-1120` 当前源码核对。阅读确认集中维护在[代码 Review 进度](../代码Review进度.md)，这里提供摘要和详解入口。

- 已读启动与通信：[Program.cs](code/server/app/Program.cs.md)、[ServerNode.Discovery.cs](code/server/engine/ServerNode.Discovery.cs.md)、[ServerNode.cs](code/server/engine/ServerNode.cs.md)、[LinkService.cs](code/server/engine/LinkService.cs.md)、[PeerLink.cs](code/server/engine/PeerLink.cs.md)。
- 已读协议与测试：[transport.proto](code/server/engine/protocols/transport.proto.md)、[RpcChecks.cs](code/tests/engine/RpcChecks.cs.md)、[NodeCommunicationChecks.cs](code/tests/engine/NodeCommunicationChecks.cs.md)、[StartupReadinessChecks.cs](code/tests/engine/StartupReadinessChecks.cs.md)。
- 已读配置与注册：[server.yaml](../../code/server/config/server.yaml)、[process.yaml](../../code/server/config/process.yaml)、[ServerConfiguration.cs](code/server/engine/configuration/ServerConfiguration.cs.md)、[ServerGroup.cs](code/server/engine/configuration/ServerGroup.cs.md)、[ServerServices.cs](code/server/app/ServerServices.cs.md)。
- `IdGenerator` 的初始化、委托和锁，以及 ZooKeeper 的初始化、占号和发现片段已讨论；整个 ID 模块、会话与租约收尾尚未确认读完。

**下一步先读 [SnowflakeIdGenerator.cs](code/server/engine/identifiers/SnowflakeIdGenerator.cs.md) 的 `CreateAsync` → `GenId` → `DisposeAsync`，再进入 `SnowflakeSequence.TryGenId`。** 以下按后续阅读顺序排列；表中链接进入详解，每份详解顶部可跳转源码。

| 顺序 | 阅读文件 | 建议方法顺序与需要弄清的问题 |
| --- | --- | --- |
| 1：ID | [SnowflakeIdGenerator.cs](code/server/engine/identifiers/SnowflakeIdGenerator.cs.md) → [SnowflakeSequence.cs](code/server/engine/identifiers/SnowflakeSequence.cs.md)，补 [IdText.cs](code/server/engine/identifiers/IdText.cs.md)、[IdGeneratorOptions.cs](code/server/engine/identifiers/IdGeneratorOptions.cs.md)，回看 [IdGenerator.cs](code/server/engine/identifiers/IdGenerator.cs.md) | worker 租约、发号与释放 → 位布局、序列耗尽和时钟回退。对照 [IdGeneratorChecks.cs](code/tests/engine/IdGeneratorChecks.cs.md)、[IdGeneratorLifecycleChecks.cs](code/tests/engine/IdGeneratorLifecycleChecks.cs.md)。 |
| 2：会话与租约 | [ZooKeeperSession.cs](code/server/engine/ZooKeeperSession.cs.md)，回看 [ZooKeeperProxy.cs](code/server/engine/ZooKeeperProxy.cs.md) 的 `AcquireWorkerAsync`、`WorkerLease` 和释放，补 [ZooKeeperOptions.cs](code/server/engine/ZooKeeperOptions.cs.md) 与 [ops.yaml](../../code/server/config/ops.yaml) | 会话失效怎样让身份与 worker 停止使用？租约如何释放？对照 [ZooKeeperProxyChecks.cs](code/tests/engine/ZooKeeperProxyChecks.cs.md)。 |
| 3：日志 | [GameLog.cs](code/server/engine/GameLog.cs.md) → [ServerLogProtocol.cs](code/server/engine/logging/ServerLogProtocol.cs.md) → [ServerLogLevel.cs](code/server/engine/logging/ServerLogLevel.cs.md) → [LogEventJson.cs](code/server/engine/logging/LogEventJson.cs.md) | 日志何时创建和释放？事件、等级与输出目标如何配合？对照 [LogLevelChecks.cs](code/tests/engine/LogLevelChecks.cs.md)。 |
| 4：辅助类型 | [ProcessType.cs](code/server/engine/configuration/ProcessType.cs.md)、[DiscoveredProcess.cs](code/server/engine/DiscoveredProcess.cs.md)、[TransportProtocol.cs](code/server/engine/TransportProtocol.cs.md)、[EngineEnvironment.cs](code/server/engine/configuration/EngineEnvironment.cs.md)、[ServerRunFiles.cs](code/server/engine/configuration/ServerRunFiles.cs.md) | 补齐类型标识、发现代次、元数据、源配置路径、本轮目录和精确停止标记；历史已读部分只复核变化。 |
| 5：配套测试 | [IocChecks.cs](code/tests/engine/IocChecks.cs.md)、[ServerConfigurationChecks.cs](code/tests/engine/ServerConfigurationChecks.cs.md)、[YamlConfigurationChecks.cs](code/tests/engine/YamlConfigurationChecks.cs.md)、[DynamicTopologyChecks.cs](code/tests/engine/DynamicTopologyChecks.cs.md)、[DiscoveryLinkChecks.cs](code/tests/engine/DiscoveryLinkChecks.cs.md)、[StartupPublicationChecks.cs](code/tests/engine/StartupPublicationChecks.cs.md) | 按模块选读准备、动作和断言；已读的三个通信测试无需重读。测试辅助类按调用回查，不要求运行测试。 |
| 6（按需）：控制台 | [ConsoleSession.cs](tools/server-console/ConsoleSession.cs.md) → [ServerInstance.cs](tools/server-console/ServerInstance.cs.md) → [RollingLog.cs](tools/server-console/RollingLog.cs.md) → [MainWindow.xaml.cs](tools/server-console/MainWindow.xaml.cs.md) | 准备可执行文件副本、直接传递源配置和本轮目录、起停进程、恢复身份、回放日志；可执行文件副本不包含配置快照。 |
| 7（按需）：存储 | [StorageConnections.cs](code/server/logic/storage/StorageConnections.cs.md) → [MongoStorage.cs](code/server/logic/storage/MongoStorage.cs.md) → [ProfileStore.cs](code/server/logic/storage/ProfileStore.cs.md)，对照 [profile.proto](code/shared/protocols/profile.proto.md) | 独立存储与业务共享协议不在当前进程启动及基础 RPC 主线中，放在框架读通之后。 |

当前基础框架约剩 15 个源码文件的阅读或复核范围，另有十来个测试可按模块选读；这是范围估算，包含已讨论片段与历史已读的复核。控制台及独立存储另计，详见[剩余范围估算](../代码Review进度.md#剩余范围估算)。每读完一个文件再更新用户确认，不因文档修正自动勾选。当前协议字段编号、配置只读加载和运行期保持首次就绪的语义，以对应详解为准；按主题的问题清单见[代码 Review 路径](../代码Review路径.md)。

## 服务器宿主（2）

| 源码 | 详解 |
| --- | --- |
| [code/server/app/Program.cs](../../code/server/app/Program.cs) | [阅读详解](code/server/app/Program.cs.md) |
| [code/server/app/ServerServices.cs](../../code/server/app/ServerServices.cs) | [阅读详解](code/server/app/ServerServices.cs.md) |

## 服务器引擎（26）

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
