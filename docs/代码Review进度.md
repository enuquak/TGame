# 代码 Review 进度

更新时间：2026-11-17（Asia/Shanghai）。

## 多实例功能续读

- 多实例改动来自 `codex/engine-multiple-instances`，已整合引擎优先初始化、可选 SinglePoint、移除 runId，以及通用 RPC、消息处理器、环境配置和雪花 ID；继续使用 `codex/server-review-snapshot` 阅读。
- 配置从角色列表改为各类型的实例数量；各实例按名称分别注册 ZooKeeper、握手和维护连接。拓扑按配置展开全部预期入站及出站实例，缺少任何一个都不能就绪，断线后撤销当前就绪状态。
- 实例名称统一从 1 开始带编号：`"Router": 2` 对应 `Router1`、`Router2`，`"Base": 1` 对应 `Base1`；角色名仍分别为 `Router`、`Base`。原无编号运行组需要停止后重新启动。
- 建议依次补读 [ServerGroup.cs](../code/shared/configuration/ServerGroup.cs)、[Topology.cs](../code/server/engine/Topology.cs)、[ServiceRegistry.cs](../code/server/engine/ServiceRegistry.cs)、[ServerNode.cs](../code/server/engine/ServerNode.cs)，再看 [Program.cs](../code/server/app/Program.cs) 的配置快照读取和启动参数。
- 验收行为见 [GroupChecks.cs](../code/tests/singlepoint/GroupChecks.cs)，配置与运行方式见 [服务器组配置](服务器组配置.md)。这些文件尚未被用户确认读完。
- 合并前多实例分支已验证：解决方案编译零警告、零错误；每种类型 2 个实例的真实 ZooKeeper/gRPC 验收通过（16 个进程、36 条连接）；控制台完成 Router2 停止/补起、配置快照恢复和增加实例后的整组重启（17 个实例）；默认配置 BAT 验收成功，重复配置键返回非零退出码。

本次合并仅验证解决方案编译、现有本地检查及启动脚本语法，未重跑整组 ZooKeeper/控制台联通验收。

通用 RPC 变更建议按 `transport.proto` → `engine/PeerLink.cs` → `engine/RpcDispatcher.cs` → `logic/messages/ServerMessages.cs` 阅读，再跟踪 Profile 或 GM 处理器；环境配置看 `app/ServerSettings.cs`。这些变更均待用户复读，不新增已读状态。

## 换机器后的续读入口

- 继续阅读的分支：`codex/server-review-snapshot`；引擎分层改动已按用户要求从 `codex/server-engine-boundary` 合入，后续 Review 调整继续在此分支进行。
- 原阅读进度基于代码提交 `4835f8d`。本次按用户要求将进程拓扑与连接管理迁入引擎，具体 SinglePoint 保留在业务层，以下链接已随迁移更新；不据此增加“已读”状态。
- 当前阶段：旧版本的服务器启动、角色初始化、服务发现和连接管理已读；本次将 SinglePoint 基础能力迁入引擎，业务实现改为可选，并改成先初始化引擎、再创建和初始化 SinglePoint。先复读这些改动，再继续消息收发与 Profile 请求处理；本次不增加“已读”状态。
- 最近一次明确确认：用户于 2026-11-14 表示上一轮推荐的文件已经看完。随后推荐的消息处理文件尚未确认读完。
- 下面的“已读”表示用户确认完成阅读，不表示这些文件已通过完整的正确性、安全或性能审查。
- 本次移除起服 `runId`：ZooKeeper 使用固定的 `/tgame/test/server-<实例名>`，重复注册直接起服失败；日志按 PID 和进程启动时间定位。补读 `ServiceRegistry.RegisterAsync`、`Program.cs` 的注册异常退出流程，以及 `TraceLog.OpenServer`；不增加“已读”状态。

## 已阅读

- [x] [Program.cs](../code/server/app/Program.cs)：启动参数、宿主配置、角色创建、初始化与注册、任务监督、退出清理。
- [x] [ServerSinglePoint.cs](../code/server/engine/ServerSinglePoint.cs)：旧版公共初始化已读；当前移入引擎并作为默认实现，复用引擎身份，不再负责等待连接，变更待复读。
- [x] [DBMgrServerSinglePoint.cs](../code/server/logic/entities/dbMgrServerSinglePoint/DBMgrServerSinglePoint.cs)：具体角色的初始化与资源释放。
- [x] [SinglePointRegistry.cs](../code/server/engine/SinglePointRegistry.cs)：旧版标注注册已读；当前移入引擎，缺少业务实现时创建默认类，变更待复读。
- [x] [Topology.cs](../code/server/engine/Topology.cs)：角色之间主动发起连接的关系。
- [x] [ServiceRegistry.cs](../code/server/engine/ServiceRegistry.cs)：向 ZooKeeper 注册地址、发现其他角色、关闭注册连接。
- [x] [ServerNode.cs](../code/server/engine/ServerNode.cs)：建立与维持连接、握手、连接状态和节点就绪条件；现为引擎基类。

## 下一步阅读顺序

先复读本次调整：

1. [Program.cs](../code/server/app/Program.cs)：`InitializeAsync` 中先 `InitializeEngineAsync`，再 `InitializeSinglePointAsync`；连接维护任务持续运行，业务初始化等待注册与首次连接完成。
2. [ServerNode.cs](../code/server/engine/ServerNode.cs)：提前创建和登记 State，连接完成后才创建 SinglePoint，Ready 仍检查业务状态与当前连接，退出时释放业务资源。
3. [SinglePointRegistry.cs](../code/server/engine/SinglePointRegistry.cs) 与 [ServerSinglePoint.cs](../code/server/engine/ServerSinglePoint.cs)：可选业务实现、默认类，以及构造/初始化失败不能回退。

建议沿一次 Profile 摘要查询，跟踪请求发出、Router 转发、Profile 处理和响应返回。先读短小的协议与入口文件，再重点阅读 `PeerLink.cs`。

| 状态 | 文件 | 阅读目标 |
| --- | --- | --- |
| 待确认 | [transport.proto](../code/shared/protocols/transport.proto) | 理解 Frame、通用 RPC 请求/响应、Any 与双向流 Connect。 |
| 待确认 | [profile.proto](../code/shared/protocols/profile.proto) | 理解请求 ID、发布/查询/撤回操作及响应结果。 |
| 待确认 | [LinkService.cs](../code/server/engine/LinkService.cs) | 理解 gRPC 连接入口、来源检查和向 ServerNode 转交连接。 |
| 待确认 | [PeerLink.cs](../code/server/engine/PeerLink.cs) | 按通用 CallAsync、WriteAsync、Receive、Close 阅读请求等待、响应匹配、超时与断线。 |
| 待确认 | [GameServerNode.cs](../code/server/logic/node/GameServerNode.cs) | 理解业务节点如何继承引擎节点；SinglePoint 生命周期已由引擎基类提供。 |
| 待确认 | [GameServerNode.Messages.cs](../code/server/logic/node/GameServerNode.Messages.cs) | 理解请求进入队列、响应直接唤醒等待者，以及消息分发。 |
| 待确认 | [PeerLink.Profile.cs](../code/server/logic/node/PeerLink.Profile.cs) | 理解业务扩展方法如何包装 Frame，以及响应实体校验。 |
| 待确认 | [GameServerNode.Profile.cs](../code/server/logic/node/GameServerNode.Profile.cs) | 理解调用入口、Router 转发、来源/实体归属校验和存储调用。 |
| 待确认 | [ProfileStore.cs](../code/server/logic/storage/ProfileStore.cs) | 理解 Redis 查询、MongoDB 读取、缓存回填、更新与删除。 |
| 待确认 | [SinglePoint 测试 Program.cs](../code/tests/singlepoint/Program.cs) | 找到 ProfileRequest 用例，对照发送请求、模拟回包和异步调用完成的过程。 |

阅读 `PeerLink.cs` 时，重点回答：

1. 为什么 `waiting` 保存 `requestId → TaskCompletionSource<Frame>`？
2. 谁调用 `TrySetResult`，让发送方的 `await reply.Task` 继续执行？
3. 请求超时、响应迟到、连接断开时，等待中的调用分别如何结束？
4. 为什么响应直接交给等待者，而请求先进入处理队列？

引擎 `ServerNode` 是基类，业务 `GameServerNode` 继承它；`GameServerNode` 的多个 partial 文件是同一个业务类的不同部分。引擎 `PeerLink` 与业务 `PeerLinkMessages` 已分开，`PeerLink.Profile.cs` 和 `PeerLink.Probes.cs` 提供业务扩展方法，不再是引擎连接类的 partial。生成协议源码用于辅助跳转，日常阅读和修改以手写 `.proto` 为准。

## 已讨论的 C# 与项目概念

- 用户有其他语言基础，C# 从接近零开始；继续采用逐段解释、对应代码示例、说明类型与执行顺序的方式。
- 各角色运行同一个 `TGame.Server` 程序，通过启动参数选择角色；各进程拥有独立内存。
- 操作系统进程先启动并执行 Program；`await app.StartAsync()` 启动 Web 宿主，业务就绪还需要初始化与连接完成。
- 异步方法调用时即可开始执行，保存 `Task` 后可以稍后等待；没有 `await` 不等于没有开始，也不等于自动创建新线程。
- `using var` 在作用域退出时同步释放；`await using var` 在退出时等待异步释放，并非异步构造。
- `CancellationTokenSource` 发出取消请求，`CancellationToken` 交给操作观察；关联取消与超时属于协作式取消。
- `peer => ...` 是 Lambda 表达式；`Select` 描述转换，`ToList` 枚举并收集结果；`Task.WhenAll` 等待任务完成。
- 已解释 FirstLinks 的首次连接完成含义、非控制台模式的启动超时检查，以及 WatchStopAsync 轮询停止标记。

以上概念说明对应当时阅读版本；切换其他功能分支后，应先核对该分支的实际实现。

## 分支交接

本地存在几条尚未整合的开发路线，本次分别上传，保持各自的提交历史。

| 分支 | 本次上传前的代码提交 | 用途 |
| --- | --- | --- |
| `codex/server-review-snapshot` | `4835f8d` | 本次对话续读入口，包含 VS Code 工程加载修复、探针实体解耦、DBMgr 配置拆分、多条件换行规范；本次再加入阅读进度。 |
| `codex/multiline-or-format` | `4835f8d` | 上传前主工作区所处的代码分支。 |
| `codex/remove-test-coupling` | `86c5e88` | 另一工作区中的诊断隔离、环境配置外置改动。 |
| `codex/server-group-counts` | `f9fcfb1` | 另一工作区中的实例数量配置、完整连接等待及历史日志校验改动。 |
| `codex/local-main-snapshot-20261115` | `26b88a4` | 本次上传时本地 main 的快照；该历史包含已在本地完成的实例数量分支合并。 |

这些分支尚未全部汇总为同一个版本。本次引擎分层已从 `codex/server-engine-boundary` 合入 `codex/server-review-snapshot`，继续阅读和调整使用后者。查看其他功能时再切换对应分支。本次不合并或推送远端 `main`，其他开发路线的整合仍需用户明确安排。

其他已有 `codex/*` 事项分支也一并上传，保留原有名称，便于定位提交。

## 新机器准备

以下命令获取远端的 `codex/server-review-snapshot`。引擎分层合并目前仅在本地，新机器需待该分支推送后再拉取最新改动。

新克隆仓库时选择原交接分支：

```powershell
git clone --branch codex/server-review-snapshot https://github.com/enuquak/TGame.git
cd TGame
```

已有克隆、尚无该本地分支时：

```powershell
git fetch origin
git switch --track origin/codex/server-review-snapshot
```

安装符合 [global.json](../global.json) 的 .NET SDK，VS Code 安装 C# 与 C# Dev Kit 扩展，并打开仓库根目录。工作区默认选择 [TGame.slnx](../TGame.slnx)。完整解决方案包含 Windows WPF 控制台，建议在 Windows 构建：

```powershell
dotnet build TGame.slnx
code .
```

仅构建服务端时，可以执行 `dotnet build code/server/app/TGame.Server.csproj`。若命令行编译通过、编辑器仍出现基础类型无法识别，执行 `Developer: Reload Window`，并确认当前解决方案为 `TGame.slnx`。

生成文件、构建产物、运行日志以及本机凭据按 `.gitignore` 保留在本机，不属于 Git 交接内容。阅读源码和构建无需运行云端联通验收；需要实际启动时，按 [服务器连接说明](服务器连接.md) 在新机器配置自己的 SSH 访问和环境隧道。Windows 脚本使用 PowerShell 7。

## 持续待办与规范

- [整合日志系统](TODO.md) 仍是待办，本次只交接进度。
- 编码偏好以 [项目协作规范](../code/agents/项目协作规范.md) 为准：完整花括号、逻辑段落空行、early return、业务标识引用常量/枚举、复杂表达式拆分、多条件 `||` 换行，以及安全前提下的变量声明排序。
- 用户确认读完下一批文件后，再更新本文件的阅读状态。
