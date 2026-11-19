# 代码 Review 进度

更新时间：2026-11-20（Asia/Shanghai）。

## 当前入口

本轮以 `review-1119` 的当前源码整理，用户要求完成文档后推送该分支并合并到 main。逐文件阅读顺序见[详解首页](代码详解/README.md#当前-review-进度与下一步)，按主题的问题清单见[代码 Review 路径](代码Review路径.md)。当前服务器只运行进程基础框架，由 App 的单个 IoC 根容器组装。

## 本轮确认记录

- [x] [code/server/app/Program.cs](../code/server/app/Program.cs)：用户于 2026-11-20 明确确认当前文件已读完。启动顺序为日志 → 配置 → IoC → ZooKeeper 会话与占号 → ID → 监听与首次连接 → 地址发布 → `PROCESS_READY`；退出先停止后台任务和监听，随后释放容器，最后释放日志。
- [x] [ServerNode.Discovery.cs](../code/server/engine/ServerNode.Discovery.cs)：用户于 2026-11-20 明确确认已读完，包括发现快照、目标筛选、任务替换、失败传播与取消清理。
- [ ] [ServerNode.cs](../code/server/engine/ServerNode.cs)：下一步。已经讨论入口拆分、Hello、RunLinkAsync、RunTransportAsync、ReadAsync 和 ServeAsync，尚未明确确认整份文件已读完。
- [ ] [PeerLink.cs](../code/server/engine/PeerLink.cs)：已讨论 CallAsync、写锁和 waiting 的登记时机，尚未明确确认整份文件已读完。
- [ ] ZooKeeperProxy / ZooKeeperSession：已解释临时占号与 `ephemeralOwner`，仍需完整阅读初始化、发现、会话失效和释放。
- [ ] ServerServices / ServerConfiguration / ServerGroup：按详解首页顺序补看依赖注册、统一配置加载、拓扑与数量来源；Program 已读不代表这些文件内部实现也已读完。
- [ ] ID、日志、运行文件和相应测试：保留待确认状态，控制台与独立存储按需继续。

只有用户明确确认完成阅读后才勾选；自动化测试和代码审查不能替代用户的阅读确认。历史测试结果与适用版本见路径文档第 7 节。

## 本轮已确定的行为

- `ConnectAsync` 和 `AcceptConnectAsync` 分别承担主动和入站入口校验；RunLinkAsync 管理 Hello、连接登记和退出，任务持续到连接结束。
- FirstLinks 首次按主动目标类型达到配置数量后完成一次，Ready 直接从其完成状态派生；运行期依赖全部断开也不撤销就绪，发现和重连继续。未连接的 RPC 返回 Unavailable，断线使待处理 RPC 失败。
- 两端都运行 ReadAsync 和 ServeAsync。接收 Request 进入本地请求队列，再由 ServeAsync 回复；接收 Reply 直接按请求 ID 完成本机等待项。
- waiting 属于主动发起 RPC 的本机 PeerLink，在 CallAsync 写出 Request 前登记，在 finally 移除；不是 ServeAsync 的请求队列。

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
