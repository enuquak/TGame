# 全项目手写源码详解

按当前代码整理，共 **76 个源码文件、76 份独立详解**。覆盖 C#、XAML 和手写 Protobuf 定义，包括测试及桌面控制台；排除工具脚本、生成代码、构建产物、第三方工具二进制、项目构建文件和配置数据。当前客户端目录尚无手写源码。

详解与源码目录对应，文件名保留原扩展名，例如 `Program.cs.md`。文档描述当前实现；测试入口说明不代表本次执行过完整测试或外部服务联调。

建议先读 App 的 Program、ServerServices，再读引擎统一配置 ServerConfiguration、ServerGroup，随后沿 ZooKeeper、ID、ServerNode 与 PeerLink 追踪启动和通信；控制台与各测试文件可按需查阅。

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
