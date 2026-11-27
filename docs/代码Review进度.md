# 代码 Review 进度

更新时间：2026-11-28（Asia/Shanghai）。

## 当前入口

当前工作分支为 `review-1128`。用户于 2026-11-28 确认：本计划中的内容除服务器控制台外基本读完；控制台只关注是否可用，考虑时间投入，不再安排实现细读。本轮阅读按“基本完成”记录，后续结合实际开发或具体问题回查。

本文是阅读确认与后续安排的记录入口；逐文件详解可从[详解首页](代码详解/README.md)查阅，按主题的问题清单见[代码 Review 路径](代码Review路径.md)。阅读状态以本文最新确认为准。当前服务器只运行进程基础框架，由 App 的单个 IoC 根容器组装。

## 本轮确认记录

- [x] **整体进度**：用户于 2026-11-28 表示“基本都看完了，除了服务器控制台部分”。以下新增完成记录依据这次整体确认，表示计划内容基本读完，不扩大为所有文件逐行精读或所有细节均已掌握。
- [x] [code/server/app/Program.cs](../code/server/app/Program.cs)：用户于 2026-11-20 明确确认当前文件已读完。启动顺序为日志 → 配置 → IoC → ZooKeeper 会话与占号 → ID → 监听与首次连接 → 地址发布 → `PROCESS_READY`；退出先停止后台任务和监听，随后释放容器，最后释放日志。
- [x] 发现与通信：[ServerNode.Discovery.cs](../code/server/engine/ServerNode.Discovery.cs)、[ServerNode.cs](../code/server/engine/ServerNode.cs)、[LinkService.cs](../code/server/engine/LinkService.cs)、[PeerLink.cs](../code/server/engine/PeerLink.cs)。在推荐发现、连接与收发主线后，用户确认“这几个文件我都看完了”。
- [x] 协议与三个测试：[transport.proto](../code/server/engine/protocols/transport.proto)、[RpcChecks.cs](../code/tests/engine/RpcChecks.cs)、[NodeCommunicationChecks.cs](../code/tests/engine/NodeCommunicationChecks.cs)、[StartupReadinessChecks.cs](../code/tests/engine/StartupReadinessChecks.cs)。用户随后明确确认“这4个文件都看完了”。
- [x] 配置主线：[server.yaml](../code/server/config/server.yaml)、[process.yaml](../code/server/config/process.yaml)、[ServerConfiguration.cs](../code/server/engine/configuration/ServerConfiguration.cs)、[ServerGroup.cs](../code/server/engine/configuration/ServerGroup.cs)。用户明确确认已读完所推荐的配置文件与实现。
- [x] [ServerServices.cs](../code/server/app/ServerServices.cs)：用户明确确认已读完依赖注册。
- [x] [ZooKeeperProxy.cs](../code/server/engine/ZooKeeperProxy.cs) / [ZooKeeperSession.cs](../code/server/engine/ZooKeeperSession.cs)：此前的局部阅读记录由 2026-11-28 的整体确认更新为基本读完，涵盖计划中的会话状态、进程身份及 worker 租约申请与释放。
- [x] **ID 模块**：用户于 2026-11-25 明确确认已读完，包括 [IdGenerator.cs](../code/server/engine/identifiers/IdGenerator.cs)、[SnowflakeIdGenerator.cs](../code/server/engine/identifiers/SnowflakeIdGenerator.cs)、[SnowflakeSequence.cs](../code/server/engine/identifiers/SnowflakeSequence.cs)、[IdText.cs](../code/server/engine/identifiers/IdText.cs) 和 [IdGeneratorOptions.cs](../code/server/engine/identifiers/IdGeneratorOptions.cs)。已结合 [IdGeneratorLifecycleChecks.cs](../code/tests/engine/IdGeneratorLifecycleChecks.cs) 讨论工厂委托、取消清理、并发发号、实例隔离和会话失效，后续按需回查。
- [x] 日志、辅助类型、计划中的配套测试与独立存储：依据 2026-11-28 的整体确认，记录为基本读完，不再保留为必读待办。
- **服务器控制台：主动跳过实现细读。** 用户明确认为“只要能用就好”，不需要具体阅读实现。保留使用与故障定位入口，不计入未完成阅读量，也不标记为实现已读或可用性已验证。

以上记录依据截至 2026-11-28 的用户确认。已读表示用户读过当时的内容，不意味着自动确认之后的每项改动；文档修正本身也不自动增加用户已读项。阅读测试不等于执行测试，历史测试结果与适用版本见路径文档第 7 节。

## 下一步与后续阅读顺序

**本轮逐文件阅读基本完成，不再指定下一份必读文件。** 后续遇到具体开发任务或疑问时，按“调用入口 → 对应实现 → 配套测试”回查相关内容。服务器控制台只在使用故障或需要修改功能时定位实现，不继续安排逐文件细读。

原计划的 **28 份详解已于 2026-11-25 全部按新的逐段教学方式重新编写**，包括 `ops.yaml`、控制台与独立存储的内容。可从[详解首页](代码详解/README.md)进入各篇。下面保留原阅读路线作为回查索引，不再表示待完成清单；阅读状态来自上述用户确认，而非文档编写完成。

1. **会话与租约**：[ZooKeeperSession.cs](../code/server/engine/ZooKeeperSession.cs)，再读 [ZooKeeperProxy.cs](../code/server/engine/ZooKeeperProxy.cs) 的 `AcquireWorkerAsync` → `WorkerLease.DisposeAsync` → `ReleaseWorkerAsync` → `DisposeAsync` / `CloseAsync`，补看 [ZooKeeperOptions.cs](../code/server/engine/ZooKeeperOptions.cs) 和 [ops.yaml](../code/server/config/ops.yaml)。对照 [ZooKeeperProxyChecks.cs](../code/tests/engine/ZooKeeperProxyChecks.cs)，理解 worker 如何占用和释放，以及会话失效为什么同时影响发号和宿主退出。
2. **日志系统**：[GameLog.cs](../code/server/engine/GameLog.cs) → [ServerLogProtocol.cs](../code/server/engine/logging/ServerLogProtocol.cs) → [ServerLogLevel.cs](../code/server/engine/logging/ServerLogLevel.cs) → [LogEventJson.cs](../code/server/engine/logging/LogEventJson.cs)，对照 [LogLevelChecks.cs](../code/tests/engine/LogLevelChecks.cs)。
3. **辅助类型与运行标记**：[ProcessType.cs](../code/server/engine/configuration/ProcessType.cs)、[DiscoveredProcess.cs](../code/server/engine/DiscoveredProcess.cs)、[TransportProtocol.cs](../code/server/engine/TransportProtocol.cs)、[EngineEnvironment.cs](../code/server/engine/configuration/EngineEnvironment.cs)、[ServerRunFiles.cs](../code/server/engine/configuration/ServerRunFiles.cs)。历史已读部分只复核变化，重点是实例身份、发现代次、元数据键和精确停止标记。
4. **配套测试按模块回查**：[IocChecks.cs](../code/tests/engine/IocChecks.cs)、[ServerConfigurationChecks.cs](../code/tests/engine/ServerConfigurationChecks.cs)、[YamlConfigurationChecks.cs](../code/tests/engine/YamlConfigurationChecks.cs)、[DynamicTopologyChecks.cs](../code/tests/engine/DynamicTopologyChecks.cs)、[DiscoveryLinkChecks.cs](../code/tests/engine/DiscoveryLinkChecks.cs)、[StartupPublicationChecks.cs](../code/tests/engine/StartupPublicationChecks.cs)。已确认的三个通信测试不重新列为必读。
5. **独立存储**：[StorageConnections.cs](../code/server/logic/storage/StorageConnections.cs) → [MongoStorage.cs](../code/server/logic/storage/MongoStorage.cs) → [ProfileStore.cs](../code/server/logic/storage/ProfileStore.cs)，随本轮整体确认记录为基本读完，后续按需回查。
6. **服务器控制台：跳过实现细读。** 只关注配置选择、启动与停止、进程状态和日志查看等使用行为。遇到具体问题时可查 [ConsoleSession.cs](../tools/server-console/ConsoleSession.cs)、[ServerInstance.cs](../tools/server-console/ServerInstance.cs)、[RollingLog.cs](../tools/server-console/RollingLog.cs) 和 [MainWindow.xaml.cs](../tools/server-console/MainWindow.xaml.cs)，不将这些文件重新列为必读任务。

## 剩余范围估算

根据 2026-11-28 的整体确认，本轮计划按基本完成处理，**不再保留固定必读待办**。原来的“约 10 个源码文件及配套测试”剩余量估算已不适用；不将“基本读完”换算成精确的逐文件完成率。

| 内容 | 当前状态 |
| --- | --- |
| 服务器基础框架、配置、ID、会话与租约、日志及辅助类型 | 基本读完，按具体问题回查 |
| 计划中的配套测试与独立存储 | 基本读完，阅读记录不代表测试已执行 |
| 服务器控制台实现 | 主动跳过，不计入剩余阅读任务 |

后续只有具体问题、代码变化或开发任务涉及的内容需要回查，不为补齐控制台阅读数量继续扩展计划。阅读源码和测试说明不需要实际执行测试。

## 本轮已确定的行为

- `ConnectAsync` 和 `AcceptConnectAsync` 分别承担主动和入站入口校验；RunLinkAsync 管理 Hello、连接登记和退出，任务持续到连接结束。
- FirstLinks 首次按主动目标类型达到配置数量后完成一次，Ready 直接从其完成状态派生；运行期依赖全部断开也不撤销就绪，发现和重连继续。未连接的 RPC 返回 Unavailable，断线使待处理 RPC 失败。
- 两端都运行 ReadAsync 和 ServeAsync。接收 Request 进入本地请求队列，再由 ServeAsync 回复；接收 Reply 直接按请求 ID 完成本机等待项。
- waiting 属于主动发起 RPC 的本机 PeerLink，在 CallAsync 写出 Request 前登记，在 finally 移除；不是 ServeAsync 的请求队列。
- 配置只从源 YAML 加载到内存，没有文件写回接口。启动器直接传源配置路径，本轮目录通过 `EngineEnvironment.RunDirectory` 传递；不保存配置快照。进程运行期间不重载，补起会重新读取源配置。
- 当前协议编号为 `Frame: hello=1/request=2/reply=3`，`RpcReply: request_id=1/error=2/payload=3`。旧对话中的字段编号与配置保存接口说明，以当前源码和详解为准。

上一轮文档核对以源码提交 `b91fbd7` 为基准，修正了配置写回接口移除后的说明、测试自行构造临时 YAML 的方式及协议字段编号，并扩写了当时的未读计划。2026-11-25 在 `review-1124` 对照当时源码重新编写原阅读计划的全部 28 份详解，按新的深度展开实际代码、语法与类型、执行顺序、状态变化、设计原因和失败清理。测试逐项说明准备、动作、断言与覆盖边界；补齐原来没有独立文档的 `ops.yaml`。该次文档重写没有自动增加已读项。2026-11-28 根据用户新的整体确认更新阅读状态与后续安排；此次仅维护进度和路径文档，未运行构建、服务器或测试。

## 上一轮确认记录

2026-11-17 用户确认当时的「工程边界和进程配置」已读完；2026-11-18 确认当时的 Program.cs 已理解。之后的配置合并与文件职责调整以当前源码和本轮确认为准。

用户于 2026-11-14 确认读完当时推荐的启动、SinglePoint 初始化、拓扑、服务发现和连接管理代码，包括 Program、ServerSinglePoint、DBMgrServerSinglePoint、SinglePointRegistry、Topology、ServiceRegistry 和 ServerNode。

此后结构已多次调整。当前 Engine 与业务 Shared 独立、实例统一编号、采用雪花 ID 与动态发现；SinglePoint 基类、标注、注册器及业务子类已删除，实体注册、路由和 Profile RPC 入口也已移除。基础 RPC 改为直接调用已连接进程；独立存储与业务协议保留。因此，上述旧版确认不自动计为新版已读。

## 阅读方式

- 用户有其他语言基础，C# 从接近零开始；采用逐段解释、对应代码示例、说明类型与执行顺序的方式。
- 用户于 2026-11-25 明确要求：仓库中的代码详解应达到本会话对 `IdGeneratorLifecycleChecks.cs` 的完整讲解深度。面向学习者写出理解过程，不能仅用职责摘要、场景表格或结论代替逐段解释；不因默认简洁偏好而省略必要内容。
- 每个重要逻辑段结合实际代码片段，依次解释 C# 语法与类型、调用和等待的执行时机、状态变化，以及这样设计的原因。委托、泛型、异步、取消、并发、资源所有权和释放等关键点应展开，并与调用方或被调用实现对应。
- 测试详解要说明准备了哪些对象、模拟什么事件、每个断言为什么应通过、失败意味着什么，以及同步和异步辅助方法如何工作；说明用例实际覆盖的范围，不把有限检查描述成完整保证。
- 简单重复代码可以合并说明；关键流程需要具体示例或执行顺序，不能只重复代码字面含义。验收时检查读者能否对照文档理解代码，文件覆盖数、篇幅和链接检查不能单独证明详解足够细致。
- [IdGeneratorLifecycleChecks.cs 详解](代码详解/code/tests/engine/IdGeneratorLifecycleChecks.cs.md) 是上述深度的具体参照。本轮已按该标准完成“下一步与后续阅读顺序”中的全部 28 份：会话与租约 5 份（含配置与测试）、日志 5 份（含测试）、辅助类型 5 份、配套测试 6 份、控制台 4 份、存储 3 份。范围之外的文档保留原有状态，不视为也已完成本轮重写。文档重写和对话讲解均不自动增加已读项。
- 已解释过 `Task` / `await`、`using` / `await using`、取消令牌、Lambda、WhenAny / WhenAll、进程独立内存，以及首次启动完成与当前连接可用性的区别；复读时结合调用链核对实际实现。
- 旧 `codex/server-review-snapshot`、`review-1117` 和 `review-1118` 的阅读记录属于历史。合并后继续沿同一详解入口阅读，不因分支变化重置已经确认的进度。

## 开始阅读前

在工作区打开 [TGame.slnx](../TGame.slnx)，需要 IDE 工程支持时安装 [global.json](../global.json) 指定的 SDK。仅阅读源码不需要构建、启动服务器或连接外部服务；需要验证代码时按项目规范选择必要的小范围检查。

实际起服或联调需要 PowerShell 7、SSH 隧道及本机凭据，见 [README](../README.md) 和[服务器连接说明](服务器连接.md)。构建产物、日志和 `.local/secrets/` 不属于 Git 交接内容。

规范以[项目协作规范](../code/agents/项目协作规范.md)为准；持续事项见 [TODO](TODO.md)。
