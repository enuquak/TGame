# 代码 Review 路径

更新时间：2026-11-28（Asia/Shanghai），当前工作分支为 `review-1128`。服务器已收敛为进程基础框架：ZooKeeper、进程连接、基础 RPC、日志和 ID。进程服务由 App 的单个 IoC 根容器组装，使用方通过构造函数接收依赖；`process.yaml` 的 `processes` 同时提供控制台初始启动数量和运行时首次连接门槛。独立存储工具与业务共享协议保留，启动不再创建实体或初始化业务服务。

用户于 2026-11-28 确认：阅读计划除服务器控制台外基本读完；控制台只关注是否可用，考虑时间投入，主动跳过实现细读。此次新增勾选按“基本读完”记录，不表示逐文件精读或已执行测试。详细确认与范围统一见[代码 Review 进度](代码Review进度.md)。

**本轮阅读基本完成，不再指定下一份必读文件，也不把控制台实现算作未完成任务。** 后续结合实际开发或具体问题，按“调用入口 → 对应实现 → 配套测试”回查。最新安排见[后续阅读安排](代码Review进度.md#下一步与后续阅读顺序)，逐文件文档见[详解首页](代码详解/README.md)。下面保留按主题组织的回查清单，不要求从第一节重读；阅读状态以进度文档为准。阅读源码无需启动服务，测试入口与历史范围放在第 7 节。

## 1. 工程边界和进程配置

- [x] [TGame.slnx](../TGame.slnx)、[宿主工程](../code/server/app/TGame.Server.csproj)、[引擎工程](../code/server/engine/TGame.Server.Engine.csproj)、[业务共享工程](../code/shared/TGame.Shared.csproj)：用户已确认工程边界。宿主分别引用 Engine 和 Shared；Engine 只引用第三方包，两套协议各自在所属项目生成。
- [x] [ProcessType.cs](../code/server/engine/configuration/ProcessType.cs)、[ServerGroup.cs](../code/server/engine/configuration/ServerGroup.cs)：角色、实例名称与数量的区别已有历史确认，本轮又确认读完 ServerGroup；实例统一使用 `Router1`、`Base1` 等带编号名称。
- [x] [EngineEnvironment.cs](../code/server/engine/configuration/EngineEnvironment.cs)、[ServerRunFiles.cs](../code/server/engine/configuration/ServerRunFiles.cs) 随辅助类型阶段基本读完，运行目录与运行文件按需回查；[ServerEnvironment.cs](../code/shared/csharp/ServerEnvironment.cs) 的职责边界已有历史确认。
- [x] [server.yaml](../code/server/config/server.yaml)、[process.yaml](../code/server/config/process.yaml)：用户确认读完入口及数量/连接配置。运维项按需对照 [ops.yaml](../code/server/config/ops.yaml)。
- [x] [ServerConfiguration.cs](../code/server/engine/configuration/ServerConfiguration.cs) 一次加载总入口与 include、展开文件内别名、严格绑定并校验核心配置、拓扑和数量。它持有配置值与类型连接图，私有 DTO 只用于绑定；App 仅注册这一配置实例及其持有的数量和选项对象，运行期间读取已加载值，不使用静态全局配置。
- [x] [ServerGroup.cs](../code/server/engine/configuration/ServerGroup.cs) 与 [process.yaml](../code/server/config/process.yaml)：连接规则必须无环；数量同时决定控制台初始启动次数和运行时首次连接门槛，通过 `GetStartupMinimum(ProcessType)` 查询。实际实例仍由 ZooKeeper 动态发现，数量不是连接上限或合法编号范围。
- [x] [ServerServices.cs](../code/server/app/ServerServices.cs)：用户确认读完单例注册、已有日志实例的归属及占号后解析进程身份的顺序。

当前配置类型只读入内存，没有文件写回接口。启动和补起直接使用源配置路径，运行目录通过 `EngineEnvironment.RunDirectory` 传递，不保存配置快照。读过旧实现的部分只需对照变化复核，不重置已读记录。

读完应能回答：所有角色为何运行同一个程序？保留的存储代码编译到哪个程序集？Engine 为什么能独立于业务 Shared 编译？DBMgr/Profile 的角色名称为何不代表启动时初始化数据库？

## 2. 进程身份与 ID

- [x] [IdGenerator.cs](../code/server/engine/identifiers/IdGenerator.cs)：用户于 2026-11-25 确认 ID 模块读完，覆盖工厂委托、初始化、共享实例、重复初始化报错及释放。
- [x] [IdGeneratorOptions.cs](../code/server/engine/identifiers/IdGeneratorOptions.cs)：ID 配置随模块完成，worker 根路径来自统一配置。
- [x] [IdText.cs](../code/server/engine/identifiers/IdText.cs)：校验无前导零的正 `long` 十进制文本，不负责生成 ID 或判断业务对象是否存在。
- [x] [SnowflakeIdGenerator.cs](../code/server/engine/identifiers/SnowflakeIdGenerator.cs)、[SnowflakeSequence.cs](../code/server/engine/identifiers/SnowflakeSequence.cs)：已完成实际生成器与算法阅读，包括时间戳、worker、序号组合、同毫秒序列耗尽、时钟回退及释放。
- [x] **会话与租约，基本读完**：[ZooKeeperSession.cs](../code/server/engine/ZooKeeperSession.cs) 的字段与通知任务 → `process` → `StateChanged` → `Invalidate` → `CheckActive`，以及 [ZooKeeperProxy.cs](../code/server/engine/ZooKeeperProxy.cs) 的 `AcquireWorkerAsync` → `WorkerLease.DisposeAsync` → `ReleaseWorkerAsync` → `DisposeAsync` / `CloseAsync`。结合 [ZooKeeperOptions.cs](../code/server/engine/ZooKeeperOptions.cs)、[ops.yaml](../code/server/config/ops.yaml) 和 [ZooKeeperProxyChecks.cs](../code/tests/engine/ZooKeeperProxyChecks.cs) 回查连接、会话超时和租约失效。

区分三种标识：进程实例名表示本次占号的进程，连接 ID 表示一次连接，RPC 请求 ID 关联一次请求与响应。进程实例编号与雪花 worker 编号独立；当前节点不生成实体身份。调用与限制见[统一 ID 生成](雪花ID.md)。

## 3. 沿启动流程读一遍

- [x] [Program.cs](../code/server/app/Program.cs)：用户于 2026-11-20 确认读完当前实现，包括宿主组装、并发任务、停止文件与会话监控、异常监督、取消和释放。
- [x] 当前 `Program.cs` 启动主线：日志创建 → 配置加载 → 构建根容器 → ZooKeeper 会话及空地址临时占号 → ID 初始化 → 解析节点和启动监听 → 首次连接数量达标 → 发布服务地址 → `PROCESS_READY`。`WriteProcessIdentity` 单独通过临时文件发布本机身份记录；不扫描业务类型，也不初始化 DBMgr/Profile 存储。配置解析和服务注册内部实现已另行确认读完。
- [x] [ServerNode.cs](../code/server/engine/ServerNode.cs)：构造函数接收进程身份、连接配置、`ServerGroup`、ZK、ID 和日志；名称和角色从身份对象派生，所需连接类型直接查询配置。核对 `FirstLinks`、`Ready`：首次按主动目标类型统计已握手的不同实例，达到各自门槛；门槛为 `0` 的类型不阻塞，无主动依赖可直接就绪，不等待入站。

`FirstLinks` 只完成一次，`Ready` 从其完成状态派生。例如 `Router: 2` 首次需两个 Router，达标后即使全部断开也保持就绪，继续接受入站并持续发现、重连；不重新要求首次数量。未连接 RPC 返回 `Unavailable`，断线待处理请求失败。门槛为 `0` 的类型不阻塞首次启动。入站连接不替代主动依赖，达到首次门槛后的额外实例不阻塞启动。

宿主监督停止请求与 ZooKeeper 会话。会话失效意味着进程实例身份和 worker 租约均不可继续使用，必须退出；普通对端断线由引擎持续重连，不等于本进程立即退出。退出时先取消并等待后台工作、停止监听，再异步释放容器；ID 先于 ZK 释放。日志由 App 提前创建并注册为已有共享实例，最后显式释放，以便记录容器构建、初始化或清理阶段的失败。

读完应能指出：谁占号，谁初始化 ID，谁等待首次连接，何时发布地址与记录进程就绪，以及哪个任务失败会触发退出。

## 4. 服务发现、拓扑与连接

- [x] [ServerConfiguration.cs](../code/server/engine/configuration/ServerConfiguration.cs)：通过实例方法 `GetDialTargets`、`Dials` 和 `ArePeers` 按类型连接规则匹配动态成员，区分主动目标与入站邻居；`ValidateAcyclic` 独立负责启动依赖的无环检查。
- [x] [ZooKeeperProxy.cs](../code/server/engine/ZooKeeperProxy.cs)、[ZooKeeperSession.cs](../code/server/engine/ZooKeeperSession.cs)：空地址占号、地址发布、成员快照和 Watch、身份校验、`SessionLost` 与释放已随本轮计划基本读完。空地址节点不参与拨号，但可证明主动方持有实例身份。`EnsurePathAsync` 负责公共根目录，`TryCreateEphemeralAsync` 复用抢占步骤，`ReadDiscoveredProcessAsync` 处理单条发现记录；进程编号与 worker 编号仍各自管理申请范围和时序。
- [x] [ServerNode.Discovery.cs](../code/server/engine/ServerNode.Discovery.cs)：用户于 2026-11-20 确认读完。成员新增、离开、地址变化和同名重建会取消旧任务并按快照收敛；首次达标后仍持续发现和维护全部匹配实例，门槛为 `0` 的目标也参与连接。
- [x] [DiscoveredProcess.cs](../code/server/engine/DiscoveredProcess.cs)：值比较与 `Generation` 的来源，关联 ZooKeeper 节点 `czxid` 和发现层的任务替换。
- [x] [LinkService.cs](../code/server/engine/LinkService.cs)：只读取 `peer`、`link` 元数据并交接；静态方向、占号与就绪校验在 `AcceptConnectAsync`，主动入口在 `ConnectAsync`。
- [x] [TransportProtocol.cs](../code/server/engine/TransportProtocol.cs)：辅助类型阶段基本读完，元数据键的统一声明按需回查。
- [x] [ServerNode.cs](../code/server/engine/ServerNode.cs)：沿连接维护、拨号、握手和读循环看成功与失败分支；`RunLinkAsync` 管理握手与连接登记/注销，`RunTransportAsync` 管理收发与取消协调；断线时清理连接、结束等待项，首次完成的就绪状态保持不变；重新连接后恢复对应通信。

读完应能回答：为什么依赖关系不能成环？Router 为何可以先发布地址？Base 为何分别按 Router 与 DBMgr 的数量等待而不等 Gate？`Router: 2` 达标后为何全部断开仍保持就绪，但相关 RPC 仍可能失败？首次达标后为何仍会连接新增 Router？

## 5. 基础 RPC：信封、请求关联与错误

- [x] [transport.proto](../code/server/engine/protocols/transport.proto)：`Frame` 分为 `Hello`、`Request` 和 `Reply`；请求用 `RequestId` 关联响应，响应中的 `Payload` 与 `Error` 二选一。`Any` 不依赖业务协议。当前 Frame 编号为 1/2/3，RpcReply 的 request_id/error/payload 编号为 1/2/3，读旧示例时核对差异。
- [x] [PeerLink.cs](../code/server/engine/PeerLink.cs)：按信封校验、`CallAsync`、写出、接收响应、读取结果和关闭顺序跟踪请求关联、超时、取消及断线。`waiting.TryAdd` 在本机发送 Request 之前执行；同一写锁同时串行化主动请求与响应写入，不持有到收到响应。
- [x] [ServerNode.cs](../code/server/engine/ServerNode.cs)：`CallPeerAsync` 选择已连接实例；`ReadAsync` 把 Request 放入本地队列、把 Reply 交给 `Receive`；`ServeAsync` 逐个取请求并回复，目前返回 `Unimplemented`。两端都运行这两个循环，ServeAsync 不为回复登记 waiting。

```mermaid
sequenceDiagram
    participant A as 调用进程
    participant L as PeerLink
    participant B as 已连接进程
    A->>L: CallPeerAsync(实例名, 请求)
    L->>L: 保存请求 ID 与等待项
    L->>B: RequestId + Payload
    B-->>L: Reply + Error(Unimplemented)
    L-->>A: 结束等待并返回错误
```

这是直接连接上的请求与响应；当前 Router 不选择业务实例、不按实体 ID 路由，也不转发到第三个进程。读完应能画出“发起调用 → 保存等待项 → 写出请求 → 接收响应 → 完成原来的 Task”，并区分业务未实现、无连接、超时、取消和断线。

## 6. 日志、控制台与独立存储工具

- [x] [ServerLogLevel.cs](../code/server/engine/logging/ServerLogLevel.cs)、[GameLog.cs](../code/server/engine/GameLog.cs)：从 `ServerConfiguration.LoggingMinimumLevel` 追到日志实例创建、注入、级别过滤、文件开启与释放。理解 App 提前创建且最后释放日志的原因。缺省为 `Info`，当前本地配置为 `DEBUG`。
- [x] [ServerLogProtocol.cs](../code/server/engine/logging/ServerLogProtocol.cs)、[LogEventJson.cs](../code/server/engine/logging/LogEventJson.cs)：事件、实例、连接、请求及等级写入 JSON 的方式；`ProcessReady` 的事件值为 `PROCESS_READY`。
- **跳过实现细读**：[控制台工程](../tools/server-console/TGame.ServerConsole.csproj)、[ConsoleSession.cs](../tools/server-console/ConsoleSession.cs)、[ServerInstance.cs](../tools/server-console/ServerInstance.cs)。根据用户 2026-11-28 的决定，控制台只关注配置选择、启动与停止、进程状态、日志查看等使用行为；出现故障或需要修改功能时再定位相关代码，不计入剩余阅读任务，也不标记为可用性已验证。
- **工具脚本按需参考**：[Import-ServerTypes.ps1](../tools/scripts/powershell/Import-ServerTypes.ps1)、[Read-ServerGroup.ps1](../tools/scripts/powershell/Read-ServerGroup.ps1)：脚本从引擎唯一声明加载基础类型，按需加载业务环境变量，不复制常量。不新增为本轮必读项。
- [x] **独立存储计划基本读完**：[StorageConnections.cs](../code/server/logic/storage/StorageConnections.cs)、[MongoStorage.cs](../code/server/logic/storage/MongoStorage.cs)、[ProfileStore.cs](../code/server/logic/storage/ProfileStore.cs)。配套 [MongoStorageSettings.cs](../code/server/engine/configuration/MongoStorageSettings.cs) 是 Engine 中的纯配置，其余是 logic 中的独立存储实现；这些实现不是当前启动阶段，也未通过进程 RPC 暴露。
- **业务声明按需参考**：[profile.proto](../code/shared/protocols/profile.proto)、[ProfileLogEvents.cs](../code/shared/csharp/ProfileLogEvents.cs)：业务协议和事件仍由 Shared 持有，不进入引擎传输层。不新增为本轮必读项。

## 7. 用测试反查理解

阅读测试不需要执行测试。优先关注准备状态、执行步骤与断言结果；完整、耗时或外部服务联调仍须按规范单独确认。

- [x] 用户已确认读完 [RpcChecks.cs](../code/tests/engine/RpcChecks.cs)、[NodeCommunicationChecks.cs](../code/tests/engine/NodeCommunicationChecks.cs)、[StartupReadinessChecks.cs](../code/tests/engine/StartupReadinessChecks.cs)。
- [x] 会话、日志、配置、IoC、动态发现及发布等计划内配套测试，随 2026-11-28 的整体确认记录为基本读完；ID 测试继续按需回查，具体文件见[回查索引](代码Review进度.md#下一步与后续阅读顺序)。下列命令是入口索引，不是运行授权或本轮执行记录。

当前检查项目为 [TGame.EngineTests.csproj](../code/tests/engine/TGame.EngineTests.csproj)，实际入口集中在 [Program.cs](../code/tests/engine/Program.cs)。按功能选择命令：

| 阅读内容 | 检查入口 |
| --- | --- |
| 进程握手与基础 RPC | `--node` 为节点通信专项；`--engine` 或无参数运行引擎检查集合 |
| 首次连接数量门槛、零门槛及断线后保持就绪 | `--connection-readiness` |
| 动态成员、后续新增实例及冗余连接 | `--dynamic-topology`、`--discovery-links` |
| IoC 共享、容器隔离与释放 | `--ioc`，不连接外部服务 |
| 配置、日志和本地会话生命周期守卫 | `--configuration`、`--logging`、`--zookeeper-session`，不连接外部服务 |
| 真实 ZooKeeper 占号、地址发布与身份校验 | `--registry`、`--startup-publication` |
| 真实 ZooKeeper worker 租约与发号 | `--id-zookeeper code/server/config/server.yaml` |
| 独立 MongoDB 配置、取消及连接失败 | `--mongo-storage`，使用本地回环测试端点，无需真实数据库 |
| 真实 MongoDB/Redis 的独立 Profile 存储 | `--profile-storage code/server/config/server.yaml`，需 ZooKeeper 与存储服务 |
| 控制台就绪状态、身份、恢复与精确停止 | [控制台检查](../tools/server-console/tests/Program.cs) 的 `--state-only`；`--identity-only` 只检查身份 |
| 完整控制台进程组联调 | [Test-ServerConsole.ps1](../tools/scripts/powershell/Test-ServerConsole.ps1)，需另行确认执行范围 |

命令示例与环境要求见[服务器启动与动态发现](服务器组配置.md)和[统一 ID 生成](雪花ID.md)。[2026-11-17 完整测试记录](2026-11-17完整测试记录.md)及 `docs/superpowers/` 中旧方案保留历史适用范围，其中实体与业务 RPC 的结果不代表当前框架已经验证。本文更新没有执行或宣称新的联调通过。

后续仅按具体问题、代码变化或开发任务回查，新增阅读记录仍以用户确认为准。已理解的 C# 基础不必从头复讲；控制台不安排实现细读。
