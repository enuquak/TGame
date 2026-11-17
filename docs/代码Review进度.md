# 代码 Review 进度

更新时间：2026-11-18（Asia/Shanghai）。

## 当前入口

当前在 `review-1117` 分支阅读，阅读顺序见[代码 Review 路径](代码Review路径.md)。最新确认范围见下方记录，源码持续调整时按当前文件核对调用和职责。

## 本轮确认记录

- [x] 第一节「工程边界和进程配置」：用户于 2026-11-17 明确确认已读完。
- [ ] 第二节：保留待确认状态，尚未逐项确认完成。
- [x] 第三节的 `Program.cs`：用户于 2026-11-18 表示已理解实现。已讲解启动组装、并发任务、初始化等待、`Task.WhenAll`、停止文件与 ZooKeeper 会话监控、异常监督、取消和资源释放；已区分普通对端断线重连与本进程退出。
- [ ] 第三节的 `ServerNode.cs`：已结合宿主讨论初始化、就绪和断线行为，尚未单独确认读完。下一步核对其构造、`FirstLinks`、`InitializeSinglePointAsync`、`Ready` 和 `Dispose`，再进入第四节的拓扑、服务发现与连接细节。

只有用户明确确认完成阅读后才勾选；自动化测试和代码审查不能替代用户的阅读确认。历史测试结果与适用版本见路径文档第 9 节。

## 上一轮确认记录

用户于 2026-11-14 确认读完当时推荐的启动、SinglePoint 初始化、拓扑、服务发现和连接管理代码，包括 Program、ServerSinglePoint、DBMgrServerSinglePoint、SinglePointRegistry、Topology、ServiceRegistry 和 ServerNode。

此后结构已调整：引擎独立、SinglePoint 实现可选、初始化分为两阶段、实例统一编号、采用雪花 ID、通用实体 RPC 接入 Profile，且 GameServerNode、GM Ping 和 Probe Ping/Pong 已删除。因此，上述旧版确认不自动计为新版已读。

## 阅读方式

- 用户有其他语言基础，C# 从接近零开始；采用逐段解释、对应代码示例、说明类型与执行顺序的方式。
- 已解释过 `Task` / `await`、`using` / `await using`、取消令牌、Lambda、`Task.WhenAll`、进程独立内存，以及首次连接与当前就绪的区别；复读时结合新路径核对实际实现。
- 旧 `codex/server-review-snapshot` 的合并交接属于历史记录。当前在 `review-1117` 分支继续阅读与开发，按该分支整体安排整合，不因更新阅读文档切回 `main`。

## 开始阅读前

在当前 `review-1117` 工作区打开 [TGame.slnx](../TGame.slnx)，需要 IDE 工程支持时安装 [global.json](../global.json) 指定的 SDK。仅阅读源码不需要构建、启动服务器或连接外部服务；需要验证代码时按项目规范选择必要的小范围检查。

实际起服或联调需要 PowerShell 7、SSH 隧道及本机凭据，见 [README](../README.md) 和[服务器连接说明](服务器连接.md)。构建产物、日志和 `.local/secrets/` 不属于 Git 交接内容。

规范以[项目协作规范](../code/agents/项目协作规范.md)为准；持续事项见 [TODO](TODO.md)。
