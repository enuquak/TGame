# 全项目手写源码与配置详解

当前收录 **75 个源码文件和 1 个配置文件，共 76 份独立详解**。源码覆盖 C#、XAML 和手写 Protobuf 定义，包括测试及桌面控制台；配置详解目前收录本轮阅读计划中的 `ops.yaml`。排除工具脚本、生成代码、构建产物、第三方工具二进制和项目构建文件。当前客户端目录尚无手写源码。

详解与源码目录对应，文件名保留原扩展名，例如 `Program.cs.md`。文档描述当前实现；测试入口说明不代表本次执行过完整测试或外部服务联调。

用户于 2026-11-25 明确了详解深度：面向有其他语言基础的 C# 初学者，结合实际代码逐段解释语法与类型、执行和等待时机、状态变化及设计原因，并展开取消、并发、异常与资源释放。测试还要说明每个场景如何准备、每个断言为何成立、失败意味着什么，以及用例的覆盖边界。具体要求见[阅读方式](../代码Review进度.md#阅读方式)，深度参照为 [IdGeneratorLifecycleChecks.cs](code/tests/engine/IdGeneratorLifecycleChecks.cs.md)。

**本轮已按这一标准重新编写下表中的全部 28 份详解**：会话与租约 5 份、日志 5 份、辅助类型 5 份、配套测试 6 份、控制台 4 份、存储 3 份。包含按需阅读的文件和 `ops.yaml`；下方全量索引中的其余文档不计入本轮重写范围。文档重写不会改变用户已读状态。

## 当前 Review 进度与下一步

更新于 2026-11-25（Asia/Shanghai），当前在 `review-1124`。阅读确认集中维护在[代码 Review 进度](../代码Review进度.md)，这里提供摘要和详解入口。

- 已读启动与通信：[Program.cs](code/server/app/Program.cs.md)、[ServerNode.Discovery.cs](code/server/engine/ServerNode.Discovery.cs.md)、[ServerNode.cs](code/server/engine/ServerNode.cs.md)、[LinkService.cs](code/server/engine/LinkService.cs.md)、[PeerLink.cs](code/server/engine/PeerLink.cs.md)。
- 已读协议与测试：[transport.proto](code/server/engine/protocols/transport.proto.md)、[RpcChecks.cs](code/tests/engine/RpcChecks.cs.md)、[NodeCommunicationChecks.cs](code/tests/engine/NodeCommunicationChecks.cs.md)、[StartupReadinessChecks.cs](code/tests/engine/StartupReadinessChecks.cs.md)。
- 已读配置与注册：[server.yaml](../../code/server/config/server.yaml)、[process.yaml](../../code/server/config/process.yaml)、[ServerConfiguration.cs](code/server/engine/configuration/ServerConfiguration.cs.md)、[ServerGroup.cs](code/server/engine/configuration/ServerGroup.cs.md)、[ServerServices.cs](code/server/app/ServerServices.cs.md)。
- 已读 ID 模块：[IdGenerator.cs](code/server/engine/identifiers/IdGenerator.cs.md)、[SnowflakeIdGenerator.cs](code/server/engine/identifiers/SnowflakeIdGenerator.cs.md)、[SnowflakeSequence.cs](code/server/engine/identifiers/SnowflakeSequence.cs.md)、[IdText.cs](code/server/engine/identifiers/IdText.cs.md)、[IdGeneratorOptions.cs](code/server/engine/identifiers/IdGeneratorOptions.cs.md)。已结合生命周期测试讨论初始化、委托、取消和并发；算法与测试按需回查。
- ZooKeeper 初始化、占号和发现片段已讨论；完整会话状态与租约释放尚未确认读完。

**下一步先读 [ZooKeeperSession.cs](code/server/engine/ZooKeeperSession.cs.md)：字段与通知任务 → `process` → `StateChanged` → `Invalidate` → `CheckActive`。** 再进入代理中的 worker 租约。以下链接均进入本轮重写后的详解，每份详解顶部可跳转源码。

| 顺序 | 阅读文件 | 建议方法顺序与需要弄清的问题 |
| --- | --- | --- |
| 1：会话与租约（5） | [ZooKeeperSession.cs](code/server/engine/ZooKeeperSession.cs.md) → [ZooKeeperProxy.cs](code/server/engine/ZooKeeperProxy.cs.md)，补 [ZooKeeperOptions.cs](code/server/engine/ZooKeeperOptions.cs.md)、[ops.yaml](code/server/config/ops.yaml.md)；对照 [ZooKeeperProxyChecks.cs](code/tests/engine/ZooKeeperProxyChecks.cs.md) | 从 `AcquireWorkerAsync` → `WorkerLease.DisposeAsync` → `ReleaseWorkerAsync` → `DisposeAsync` / `CloseAsync` 理解租约，再回查初始化、占号和发现。区分任务完成与连接成功、本地失效与服务端临时节点删除。 |
| 2：日志（5） | [GameLog.cs](code/server/engine/GameLog.cs.md) → [ServerLogProtocol.cs](code/server/engine/logging/ServerLogProtocol.cs.md) → [ServerLogLevel.cs](code/server/engine/logging/ServerLogLevel.cs.md) → [LogEventJson.cs](code/server/engine/logging/LogEventJson.cs.md)；对照 [LogLevelChecks.cs](code/tests/engine/LogLevelChecks.cs.md) | 日志何时创建、绑定身份和释放？事件、等级、JSON 字段和输出目标如何配合？测试怎样验证过滤和格式？ |
| 3：辅助类型（5） | [ProcessType.cs](code/server/engine/configuration/ProcessType.cs.md)、[DiscoveredProcess.cs](code/server/engine/DiscoveredProcess.cs.md)、[TransportProtocol.cs](code/server/engine/TransportProtocol.cs.md)、[EngineEnvironment.cs](code/server/engine/configuration/EngineEnvironment.cs.md)、[ServerRunFiles.cs](code/server/engine/configuration/ServerRunFiles.cs.md) | 补齐实例身份、发现代次、元数据键、源配置路径、本轮目录和精确停止标记；历史已读部分只复核变化。 |
| 4：配套测试（6） | [IocChecks.cs](code/tests/engine/IocChecks.cs.md)、[ServerConfigurationChecks.cs](code/tests/engine/ServerConfigurationChecks.cs.md)、[YamlConfigurationChecks.cs](code/tests/engine/YamlConfigurationChecks.cs.md)、[DynamicTopologyChecks.cs](code/tests/engine/DynamicTopologyChecks.cs.md)、[DiscoveryLinkChecks.cs](code/tests/engine/DiscoveryLinkChecks.cs.md)、[StartupPublicationChecks.cs](code/tests/engine/StartupPublicationChecks.cs.md) | 按模块跟随对象准备、触发动作、逐项断言、辅助方法和清理；区分本地检查与依赖真实服务的检查。已读的三个通信测试无需重读。 |
| 5（按需）：控制台（4） | [ConsoleSession.cs](tools/server-console/ConsoleSession.cs.md) → [ServerInstance.cs](tools/server-console/ServerInstance.cs.md) → [RollingLog.cs](tools/server-console/RollingLog.cs.md) → [MainWindow.xaml.cs](tools/server-console/MainWindow.xaml.cs.md) | 构建副本与源配置如何传递？如何恢复进程身份、精确停止、增量读取日志？UI 事件和后台任务怎样配合？ |
| 6（按需）：存储（3） | [StorageConnections.cs](code/server/logic/storage/StorageConnections.cs.md) → [MongoStorage.cs](code/server/logic/storage/MongoStorage.cs.md) → [ProfileStore.cs](code/server/logic/storage/ProfileStore.cs.md) | 连接、凭据、Mongo 替换与 upsert、Redis 缓存及条件回填如何执行？理解异常和一致性边界；这些工具尚未接入当前进程启动主线。 |

ID 模块已完成，当前基础框架约剩 10 个源码文件的阅读或复核范围，另含已有 ZooKeeper 代理方法与选项回顾；配套测试、控制台和存储另计。这是阅读范围估算，与本轮重写的 28 份文档数量口径不同，详见[剩余范围估算](../代码Review进度.md#剩余范围估算)。每读完一个文件再更新用户确认，不因文档修正自动勾选。按主题的问题清单见[代码 Review 路径](../代码Review路径.md)。

## 服务器宿主（2）

| 源码 | 详解 |
| --- | --- |
| [code/server/app/Program.cs](../../code/server/app/Program.cs) | [阅读详解](code/server/app/Program.cs.md) |
| [code/server/app/ServerServices.cs](../../code/server/app/ServerServices.cs) | [阅读详解](code/server/app/ServerServices.cs.md) |

## 运维配置（1）

| 配置 | 详解 |
| --- | --- |
| [code/server/config/ops.yaml](../../code/server/config/ops.yaml) | [阅读详解](code/server/config/ops.yaml.md) |

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
