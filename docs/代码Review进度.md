# 代码 Review 进度

更新时间：2026-11-24（Asia/Shanghai）。

## 当前入口

本轮按 `review-1120` 分支当前源码更新；按用户安排，完成文档后推送该分支、合入并推送 `main`，再从更新后的 `main` 创建 `review-1124` 继续。本文是阅读确认的记录入口；逐文件详解及下一步入口见[详解首页](代码详解/README.md#当前-review-进度与下一步)，按主题的问题清单见[代码 Review 路径](代码Review路径.md)。当前服务器只运行进程基础框架，由 App 的单个 IoC 根容器组装。

## 本轮确认记录

- [x] [code/server/app/Program.cs](../code/server/app/Program.cs)：用户于 2026-11-20 明确确认当前文件已读完。启动顺序为日志 → 配置 → IoC → ZooKeeper 会话与占号 → ID → 监听与首次连接 → 地址发布 → `PROCESS_READY`；退出先停止后台任务和监听，随后释放容器，最后释放日志。
- [x] 发现与通信：[ServerNode.Discovery.cs](../code/server/engine/ServerNode.Discovery.cs)、[ServerNode.cs](../code/server/engine/ServerNode.cs)、[LinkService.cs](../code/server/engine/LinkService.cs)、[PeerLink.cs](../code/server/engine/PeerLink.cs)。在推荐发现、连接与收发主线后，用户确认“这几个文件我都看完了”。
- [x] 协议与三个测试：[transport.proto](../code/server/engine/protocols/transport.proto)、[RpcChecks.cs](../code/tests/engine/RpcChecks.cs)、[NodeCommunicationChecks.cs](../code/tests/engine/NodeCommunicationChecks.cs)、[StartupReadinessChecks.cs](../code/tests/engine/StartupReadinessChecks.cs)。用户随后明确确认“这4个文件都看完了”。
- [x] 配置主线：[server.yaml](../code/server/config/server.yaml)、[process.yaml](../code/server/config/process.yaml)、[ServerConfiguration.cs](../code/server/engine/configuration/ServerConfiguration.cs)、[ServerGroup.cs](../code/server/engine/configuration/ServerGroup.cs)。用户明确确认已读完所推荐的配置文件与实现。
- [x] [ServerServices.cs](../code/server/app/ServerServices.cs)：用户明确确认已读完依赖注册。
- [ ] [ZooKeeperProxy.cs](../code/server/engine/ZooKeeperProxy.cs) / [ZooKeeperSession.cs](../code/server/engine/ZooKeeperSession.cs)：发现主线已追到 `WatchProcessesAsync`，也讨论过初始化、占号、临时节点及 `ephemeralOwner`；完整会话状态和 worker 租约释放仍待确认，不把局部阅读等同于整份文件完成。
- [ ] [IdGenerator.cs](../code/server/engine/identifiers/IdGenerator.cs)：已解释构造函数委托、`InitializeAsync`、异步互斥和创建结果发布；没有明确确认整个 ID 模块已读完。
- [ ] 日志、辅助类型与其余测试：保留待确认或当前版本复核状态。控制台和独立存储按需继续。

以上记录依据本会话截至 2026-11-24 的明确确认。已读表示用户读过当时的文件，不意味着自动确认之后的每项改动；本轮文档修正也不自动增加用户已读项。历史测试结果与适用版本见路径文档第 7 节。

## 下一步与后续阅读顺序

**下一步先读 [SnowflakeIdGenerator.cs](../code/server/engine/identifiers/SnowflakeIdGenerator.cs)：`CreateAsync` → `GenId` → `DisposeAsync`。** 从已经讨论过的 `IdGenerator.InitializeAsync` 进入实际生成器，再下钻到算法；无需重新从 Program 开始。

1. **ID 模块**：[SnowflakeSequence.cs](../code/server/engine/identifiers/SnowflakeSequence.cs) 的 `TryGenId`，再补 [IdText.cs](../code/server/engine/identifiers/IdText.cs)、[IdGeneratorOptions.cs](../code/server/engine/identifiers/IdGeneratorOptions.cs)，回看 [IdGenerator.cs](../code/server/engine/identifiers/IdGenerator.cs) 的生命周期。对照 [IdGeneratorChecks.cs](../code/tests/engine/IdGeneratorChecks.cs) 与 [IdGeneratorLifecycleChecks.cs](../code/tests/engine/IdGeneratorLifecycleChecks.cs)。重点是位布局、同毫秒序列、时钟回退、租约有效性及释放。
2. **会话与租约**：[ZooKeeperSession.cs](../code/server/engine/ZooKeeperSession.cs)，回到 [ZooKeeperProxy.cs](../code/server/engine/ZooKeeperProxy.cs) 的 `AcquireWorkerAsync`、`WorkerLease` 与释放路径，补看 [ZooKeeperOptions.cs](../code/server/engine/ZooKeeperOptions.cs) 和 [ops.yaml](../code/server/config/ops.yaml)。对照 [ZooKeeperProxyChecks.cs](../code/tests/engine/ZooKeeperProxyChecks.cs)，区分普通对端断线与本进程会话失效。
3. **日志系统**：[GameLog.cs](../code/server/engine/GameLog.cs) → [ServerLogProtocol.cs](../code/server/engine/logging/ServerLogProtocol.cs) → [ServerLogLevel.cs](../code/server/engine/logging/ServerLogLevel.cs) → [LogEventJson.cs](../code/server/engine/logging/LogEventJson.cs)，对照 [LogLevelChecks.cs](../code/tests/engine/LogLevelChecks.cs)。
4. **辅助类型与运行标记**：[ProcessType.cs](../code/server/engine/configuration/ProcessType.cs)、[DiscoveredProcess.cs](../code/server/engine/DiscoveredProcess.cs)、[TransportProtocol.cs](../code/server/engine/TransportProtocol.cs)、[EngineEnvironment.cs](../code/server/engine/configuration/EngineEnvironment.cs)、[ServerRunFiles.cs](../code/server/engine/configuration/ServerRunFiles.cs)。历史已读部分只复核变化，重点是实例身份、发现代次、元数据键和精确停止标记。
5. **配套测试按模块补读**：[IocChecks.cs](../code/tests/engine/IocChecks.cs)、[ServerConfigurationChecks.cs](../code/tests/engine/ServerConfigurationChecks.cs)、[YamlConfigurationChecks.cs](../code/tests/engine/YamlConfigurationChecks.cs)、[DynamicTopologyChecks.cs](../code/tests/engine/DynamicTopologyChecks.cs)、[DiscoveryLinkChecks.cs](../code/tests/engine/DiscoveryLinkChecks.cs)、[StartupPublicationChecks.cs](../code/tests/engine/StartupPublicationChecks.cs)。已确认的三个通信测试不重新列为必读。
6. **按需扩展**：控制台从 [ConsoleSession.cs](../tools/server-console/ConsoleSession.cs) → [ServerInstance.cs](../tools/server-console/ServerInstance.cs) → [RollingLog.cs](../tools/server-console/RollingLog.cs) → [MainWindow.xaml.cs](../tools/server-console/MainWindow.xaml.cs) 阅读；独立存储从 [StorageConnections.cs](../code/server/logic/storage/StorageConnections.cs) → [MongoStorage.cs](../code/server/logic/storage/MongoStorage.cs) → [ProfileStore.cs](../code/server/logic/storage/ProfileStore.cs) 阅读。

## 剩余范围估算

按当前服务器基础框架估算，还剩三个主要模块和一组辅助类型，约 **15 个源码文件的阅读或复核范围**，不是精确的未读文件计数；已讨论片段和历史已读内容可快速回顾。

| 内容 | 大致范围 |
| --- | --- |
| ID 生成 | 5 个源码文件 |
| ZooKeeper 会话与租约 | 1 个会话文件，加已有代理方法及选项回顾 |
| 日志系统 | 4 个源码文件 |
| 基础辅助类型 | 约 5 个短文件，历史已读部分只核对变化 |

配套测试另有十来个，跟随模块选择阅读。控制台还有十几个源码文件，独立存储有 3 个主要源码文件，均不计入上述基础框架估算。阅读源码和测试说明不需要实际执行测试。

## 本轮已确定的行为

- `ConnectAsync` 和 `AcceptConnectAsync` 分别承担主动和入站入口校验；RunLinkAsync 管理 Hello、连接登记和退出，任务持续到连接结束。
- FirstLinks 首次按主动目标类型达到配置数量后完成一次，Ready 直接从其完成状态派生；运行期依赖全部断开也不撤销就绪，发现和重连继续。未连接的 RPC 返回 Unavailable，断线使待处理 RPC 失败。
- 两端都运行 ReadAsync 和 ServeAsync。接收 Request 进入本地请求队列，再由 ServeAsync 回复；接收 Reply 直接按请求 ID 完成本机等待项。
- waiting 属于主动发起 RPC 的本机 PeerLink，在 CallAsync 写出 Request 前登记，在 finally 移除；不是 ServeAsync 的请求队列。
- 配置只从源 YAML 加载到内存，没有文件写回接口。启动器直接传源配置路径，本轮目录通过 `EngineEnvironment.RunDirectory` 传递；不保存配置快照。进程运行期间不重载，补起会重新读取源配置。
- 当前协议编号为 `Frame: hello=1/request=2/reply=3`，`RpcReply: request_id=1/error=2/payload=3`。旧对话中的字段编号与配置保存接口说明，以当前源码和详解为准。

本轮文档核对以源码提交 `b91fbd7` 为基准，修正配置写回接口移除后的说明、测试自行构造临时 YAML 的方式及协议字段编号。按用户要求，将未读计划中的 ID、会话租约、日志、辅助类型、配套测试与可选控制台/存储入口扩写为逐段流程、字段和状态说明、C# 语法短例、异常及清理路径。阅读进度保留用户确认，文档扩写不新增已读项；本轮未运行服务器或联调测试。

## 上一轮确认记录

2026-11-17 用户确认当时的「工程边界和进程配置」已读完；2026-11-18 确认当时的 Program.cs 已理解。之后的配置合并与文件职责调整以当前源码和本轮确认为准。

用户于 2026-11-14 确认读完当时推荐的启动、SinglePoint 初始化、拓扑、服务发现和连接管理代码，包括 Program、ServerSinglePoint、DBMgrServerSinglePoint、SinglePointRegistry、Topology、ServiceRegistry 和 ServerNode。

此后结构已多次调整。当前 Engine 与业务 Shared 独立、实例统一编号、采用雪花 ID 与动态发现；SinglePoint 基类、标注、注册器及业务子类已删除，实体注册、路由和 Profile RPC 入口也已移除。基础 RPC 改为直接调用已连接进程；独立存储与业务协议保留。因此，上述旧版确认不自动计为新版已读。

## 阅读方式

- 用户有其他语言基础，C# 从接近零开始；采用逐段解释、对应代码示例、说明类型与执行顺序的方式。
- 已解释过 `Task` / `await`、`using` / `await using`、取消令牌、Lambda、WhenAny / WhenAll、进程独立内存，以及首次启动完成与当前连接可用性的区别；复读时结合调用链核对实际实现。
- 旧 `codex/server-review-snapshot`、`review-1117` 和 `review-1118` 的阅读记录属于历史。合并后继续沿同一详解入口阅读，不因分支变化重置已经确认的进度。

## 开始阅读前

在工作区打开 [TGame.slnx](../TGame.slnx)，需要 IDE 工程支持时安装 [global.json](../global.json) 指定的 SDK。仅阅读源码不需要构建、启动服务器或连接外部服务；需要验证代码时按项目规范选择必要的小范围检查。

实际起服或联调需要 PowerShell 7、SSH 隧道及本机凭据，见 [README](../README.md) 和[服务器连接说明](服务器连接.md)。构建产物、日志和 `.local/secrets/` 不属于 Git 交接内容。

规范以[项目协作规范](../code/agents/项目协作规范.md)为准；持续事项见 [TODO](TODO.md)。
