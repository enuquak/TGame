# 代码 Review 路径

更新时间：2026-11-17（Asia/Shanghai）。阅读基线：当前 `review-1117` 分支，包含 Engine 与业务 Shared 分离后的代码。

这份路径按当前代码重新整理。下面均为待阅读或待复读；测试通过不代表用户已读完。上一轮确认记录保留在[代码 Review 进度](代码Review进度.md)，[完整测试记录](2026-11-17完整测试记录.md)记录的是本次依赖拆分前的完整验证，不代表拆分后重新跑过完整测试。本次修改只做相关编译和短时检查，范围见[迁移计划与执行记录](superpowers/plans/2026-11-17-engine-business-shared.md)。

## 1. 工程边界和进程配置

- [ ] [TGame.slnx](../TGame.slnx)、[宿主工程](../code/server/app/TGame.Server.csproj)、[引擎工程](../code/server/engine/TGame.Server.Engine.csproj)、[业务共享工程](../code/shared/TGame.Shared.csproj)：确认宿主分别引用 Engine 和 Shared；Engine 只引用第三方包，Engine 与业务 Shared 之间没有项目引用。两套协议各自在所属项目生成。
- [ ] [ProcessType.cs](../code/server/engine/configuration/ProcessType.cs)、[ServerGroup.cs](../code/server/engine/configuration/ServerGroup.cs)、[processes.json](../code/server/config/processes.json)：现有角色体系归引擎，区分进程类型、实例名称与实例数量；实例由 `ServerProcess.FormatName` 统一生成 `Router1`、`Base1` 等带编号名称。
- [ ] [EngineEnvironment.cs](../code/server/engine/configuration/EngineEnvironment.cs)、[ServerRunFiles.cs](../code/server/engine/configuration/ServerRunFiles.cs)、[ServerEnvironment.cs](../code/shared/csharp/ServerEnvironment.cs)：区分引擎日志目录、运行标记与宿主业务配置、存储凭据变量。
- [ ] [ServerSettings.cs](../code/server/app/ServerSettings.cs)、[server.local.yaml](../code/server/config/server.local.yaml)：理解环境选择、配置快照、凭据变量名与配置校验。

读完应能回答：所有角色为何运行同一个程序？业务代码编译到哪个程序集？`ServerMessages` 为什么能作为业务类型扫描入口？移除业务 Shared 后 Engine 为什么仍能独立编译？

## 2. 身份与 SinglePoint

- [ ] [IIdGenerator.cs](../code/server/engine/identifiers/IIdGenerator.cs)、[IdGeneratorOptions.cs](../code/server/engine/identifiers/IdGeneratorOptions.cs)、[id-generator.json](../code/server/config/id-generator.json)：先看接口和参数来源。
- [ ] [SnowflakeSequence.cs](../code/server/engine/identifiers/SnowflakeSequence.cs)、[SnowflakeIdGenerator.cs](../code/server/engine/identifiers/SnowflakeIdGenerator.cs)、[IdText.cs](../code/server/engine/identifiers/IdText.cs)：再看序列生成、时钟回退、ZooKeeper 编号占用、会话失效及 ID 文本校验。
- [ ] [SinglePointState.cs](../code/server/engine/SinglePointState.cs)、[ServerSinglePoint.cs](../code/server/engine/ServerSinglePoint.cs)：理解引擎先分配身份，业务 SinglePoint 复用身份。
- [ ] [SinglePointForAttribute.cs](../code/server/engine/SinglePointForAttribute.cs)、[SinglePointRegistry.cs](../code/server/engine/SinglePointRegistry.cs)：理解标注注册、默认实现、重复映射和初始化失败。

读完应能回答：进程名称、连接 ID、RPC 请求 ID、实体 ID 分别表示什么？没有业务 SinglePoint 子类的进程为什么也能启动？

## 3. 沿启动流程读一遍

- [ ] [Program.cs](../code/server/app/Program.cs)：从参数解析进入 `RunServerAsync`，跟踪节点组装、监听、ZooKeeper 注册、连接维护、`InitializeEngineAsync`、`InitializeSinglePointAsync` 和退出监督。
- [ ] [ServerNode.cs](../code/server/engine/ServerNode.cs)：重点看构造函数、`FirstLinks`、`Ready`、`InitializeSinglePointAsync`、`Dispose`。

当前顺序为：生成身份 → 监听与服务注册 → 建立首次拓扑连接 → 创建并初始化 SinglePoint → 连接齐备且业务初始化成功后就绪。`FirstLinks` 表示首次连接齐备，`Ready` 反映当前状态，断线会撤销当前就绪状态。

宿主直接创建 `ServerNode`。当前不存在 `GameServerNode`；具体业务实现放在 `logic/entities/SinglePoint/`。

## 4. 服务发现、拓扑与连接

- [ ] [Topology.cs](../code/server/engine/Topology.cs)：从类型间连接关系展开每个实例的连接目标和预期邻居。
- [ ] [ServiceRegistry.cs](../code/server/engine/ServiceRegistry.cs)：跟踪 `RegisterAsync`、`ResolveAsync`、重复注册失败及会话释放。
- [ ] [TransportProtocol.cs](../code/server/engine/TransportProtocol.cs)、[LinkService.cs](../code/server/engine/LinkService.cs)：理解接入元数据、来源检查和连接交接。
- [ ] 回到 [ServerNode.cs](../code/server/engine/ServerNode.cs)：按 `MaintainLinkAsync` → `ConnectAsync` → `RunLinkAsync` 阅读握手、实体登记、断开清理与就绪更新。

读完应能回答：进程地址来自哪里？实体归属在哪里维护？为什么少一个配置实例也不能就绪？Base 与 DBMgr 的现有长连接和 Router 转发各自承担什么？

## 5. 通用 RPC：先看信封，再看等待与回包

- [ ] [transport.proto](../code/server/engine/protocols/transport.proto)：区分握手、请求、响应；理解 `Any`、目标、来源、截止时间和错误结果。通用协议编译到 Engine，不导入业务协议。
- [ ] [EntityRpcContext.cs](../code/server/engine/EntityRpcContext.cs)：区分原始调用进程 `SourceProcess` 与本跳来源 `PeerProcess`。
- [ ] [PeerLink.cs](../code/server/engine/PeerLink.cs)：按 `CallAsync`、`WriteAsync`、`Receive`、`ReadReply`、`Close` 跟踪请求关联、超时、取消和断线。
- [ ] [RpcDispatcher.cs](../code/server/engine/RpcDispatcher.cs)：比较普通消息注册与 `RegisterEntity`；理解可选的转发前校验回调、业务拒绝响应和未注册消息错误。
- [ ] 回到 [ServerNode.cs](../code/server/engine/ServerNode.cs)：看读循环如何直接处理响应、请求如何排队，以及实体请求如何支持嵌套调用。

读完应能画出：发起调用 → 保存等待项 → 写出请求 → 接收响应 → 找到等待项 → 完成原来的 `Task`。

## 6. 实体与 SinglePoint 路由

- [ ] [ServerNode.Routing.cs](../code/server/engine/ServerNode.Routing.cs)：依次阅读 `CallEntityAsync`、`CallSinglePointAsync`、`CreateRequest`、`RouteReplyAsync`、`RouteRequestAsync`。
- [ ] 再看同文件的 `ValidateSource`、`SelectSinglePoint`、`SelectNextLink`、SinglePoint 登记与移除，最后看错误转换。

重点区分：

- `CallEntityAsync` 的目标 ID 表示执行方法的实体。
- `CallSinglePointAsync(ProcessType.Profile, …)` 由 Router 从已登记的 Profile SinglePoint 中随机选择一次，再按实体 ID 投递。
- Gate 发出的实体调用可经现有 Login/Base 连接接入；对端没有目标实体时经 Router 转发。Gate 上的实体不作为 RPC 目标，Login/Base 向 Gate 发消息仍走已有连接。
- 当前没有增加 Router 之间的网状连接，也没有新增 Gate 经 Base 调用 DBMgr 的业务流程。

读完应能回答：类型何时变成具体实体 ID？原始来源如何保持？目标不存在、未就绪和回包失败分别如何结束调用？

## 7. 以一次 Profile 查询串起业务链路

- [ ] [profile.proto](../code/shared/protocols/profile.proto)：发布、查询、撤回及结果枚举。
- [ ] [ServerMessages.cs](../code/server/logic/messages/ServerMessages.cs)、[ProfileMessages.cs](../code/server/logic/messages/ProfileMessages.cs)：先看注册，再看 `CallProfileAsync`、`ValidateRoute`、`HandleAsync`、参数与响应校验。
- [ ] [ProfileServerSinglePoint.cs](../code/server/logic/entities/SinglePoint/ProfileServerSinglePoint.cs)：存储初始化、请求执行、异常转换与释放。
- [ ] [ProfileStore.cs](../code/server/logic/storage/ProfileStore.cs)、[StorageConnections.cs](../code/server/logic/storage/StorageConnections.cs)：MongoDB 持久化、Redis 命中和回填、缓存占用标记、更新后的缓存失效。

```mermaid
sequenceDiagram
    participant L as Login / Base 调用端
    participant R as Router
    participant P as Profile SinglePoint
    participant S as ProfileStore
    L->>R: CallSinglePointAsync(Profile, request)
    R->>R: ProfileMessages 校验来源、参数和写入归属
    R->>R: 选择 Profile 实体并路由
    R->>P: 通用 RPC 请求
    P->>S: HandleAsync
    S-->>P: ProfileReply
    P-->>R: 通用 RPC 回包
    R-->>L: 完成调用
```

RPC 目标是执行方法的 **Profile SinglePoint**，业务请求中的实体 ID 是要读写的 **摘要所属实体**。通用 RPC 的请求 ID 负责运输关联，Profile 的业务请求 ID 原样保留在业务响应中。

写入归属检查仍在 Router 上执行，但规则定义在 `ProfileMessages`。引擎只调用通用校验回调。Profile 服务端要求请求来自 Router；调用端保留 `Rejected` / `Unavailable` 等业务结果及响应 ID 校验。

## 8. DBMgr、日志和控制台

- [ ] [DBMgrServerSinglePoint.cs](../code/server/logic/entities/SinglePoint/DBMgrServerSinglePoint.cs)、[MongoStorageSettings.cs](../code/server/logic/storage/MongoStorageSettings.cs)、[MongoStorage.cs](../code/server/logic/storage/MongoStorage.cs)、[dbmgr-storage.yaml](../code/server/config/dbmgr-storage.yaml)：独立存储配置、凭据、连接检查与失败就绪状态。
- [ ] [ServerLogProtocol.cs](../code/server/engine/logging/ServerLogProtocol.cs)、[LogEventJson.cs](../code/server/engine/logging/LogEventJson.cs)、[TraceLog.cs](../code/server/engine/TraceLog.cs)、[ProfileLogEvents.cs](../code/shared/csharp/ProfileLogEvents.cs)：通用日志设施属于 Engine，Profile 业务事件属于 Shared；日志字段和值保持原格式。
- [ ] [控制台工程](../tools/server-console/TGame.ServerConsole.csproj)、[ConsoleSession.cs](../tools/server-console/ConsoleSession.cs)、[ServerInstance.cs](../tools/server-console/ServerInstance.cs)：控制台分别引用 Engine 与业务 Shared；先看两套协议导出，再看起服、选关、补起、接管、配置快照、编译并重启及状态计算。
- [ ] [Import-ServerTypes.ps1](../tools/scripts/powershell/Import-ServerTypes.ps1)、[Read-ServerGroup.ps1](../tools/scripts/powershell/Read-ServerGroup.ps1)：脚本从引擎的唯一声明加载进程与基础类型，同时按需加载业务环境变量，不复制常量。
- [ ] [MainWindow.xaml.cs](../tools/server-console/MainWindow.xaml.cs)、[MainWindow.Logs.cs](../tools/server-console/MainWindow.Logs.cs)、[ProcessPage.cs](../tools/server-console/ProcessPage.cs)：最后看操作入口与日志展示。

当前 GM 使用默认 SinglePoint，保留拓扑连接；GM Ping 命令和 Probe Ping/Pong 已移除，不再沿旧诊断入口阅读。

## 9. 用测试反查理解

| 想确认的行为 | 对应检查 |
| --- | --- |
| Engine 不引用业务 Shared、通用协议和基础类型归属正确 | [RpcChecks.CheckTransportBoundary](../code/tests/singlepoint/RpcChecks.cs)，随 `--engine` 小范围检查运行 |
| 默认实现、注册错误与两阶段初始化 | [SinglePointChecks.cs](../code/tests/singlepoint/SinglePointChecks.cs)、[Program.cs](../code/tests/singlepoint/Program.cs) |
| ID 并发、回退、会话丢失与真实编号分配 | [IdGeneratorChecks.cs](../code/tests/singlepoint/IdGeneratorChecks.cs) |
| 服务注册冲突与重新注册 | [ServiceRegistryChecks.cs](../code/tests/singlepoint/ServiceRegistryChecks.cs) |
| 消息关联、嵌套调用、排队与取消 | [RpcChecks.cs](../code/tests/singlepoint/RpcChecks.cs)、[MessageDispatchChecks.cs](../code/tests/singlepoint/MessageDispatchChecks.cs) |
| Gate 接入、实例选择、错误和路由清理 | [EntityRoutingChecks.cs](../code/tests/singlepoint/EntityRoutingChecks.cs)、[GroupChecks.cs](../code/tests/singlepoint/GroupChecks.cs) |
| Profile 写入归属、参数校验与回包 | [ProfileRpcChecks.cs](../code/tests/singlepoint/ProfileRpcChecks.cs) |
| 真实 MongoDB/Redis 与 Profile 服务处理 | [ProfileStorageChecks.cs](../code/tests/singlepoint/ProfileStorageChecks.cs) |
| 配置、日志、起停、恢复与编译重启 | [控制台检查](../tools/server-console/tests/Program.cs)、[Test-ServerConsole.ps1](../tools/scripts/powershell/Test-ServerConsole.ps1) |
| 从无生成文件的目录导出引擎和业务两套协议 | [控制台检查](../tools/server-console/tests/Program.cs) 的 `--export-only` 模式；仅在带 `.console-check` 标记的隔离副本使用，不启动服务器 |

建议从第 1 节开始，每次完成一个小节后记录问题与确认结果；优先理解第 3～7 节的实际执行顺序，再深入辅助实现。
