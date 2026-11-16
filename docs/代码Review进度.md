# 代码 Review 进度

更新时间：2026-11-17（Asia/Shanghai）。

## 当前入口

新的阅读顺序见[代码 Review 路径](代码Review路径.md)。本轮按现有结构重新整理，阅读基线为提交信息 `1117review后修改` 对应的本地 `main` 合并结果。测试范围与结果见[完整测试记录](2026-11-17完整测试记录.md)。

本轮路径全部标为待读或待复读。只有用户明确确认完成阅读后才勾选；自动化测试和代码审查不能替代用户的阅读确认。

## 上一轮确认记录

用户于 2026-11-14 确认读完当时推荐的启动、SinglePoint 初始化、拓扑、服务发现和连接管理代码，包括 Program、ServerSinglePoint、DBMgrServerSinglePoint、SinglePointRegistry、Topology、ServiceRegistry 和 ServerNode。

此后结构已调整：引擎独立、SinglePoint 实现可选、初始化分为两阶段、实例统一编号、采用雪花 ID、通用实体 RPC 接入 Profile，且 GameServerNode、GM Ping 和 Probe Ping/Pong 已删除。因此，上述旧版确认不自动计为新版已读。

## 阅读方式

- 用户有其他语言基础，C# 从接近零开始；采用逐段解释、对应代码示例、说明类型与执行顺序的方式。
- 已解释过 `Task` / `await`、`using` / `await using`、取消令牌、Lambda、`Task.WhenAll`、进程独立内存，以及首次连接与当前就绪的区别；复读时结合新路径核对实际实现。
- 当前会话已授权将 `codex/server-review-snapshot` 合入本地 `main`；不再使用旧分支交接表作为阅读入口。远端是否包含本次结果须以实际推送状态为准。

## 开始阅读前

在拥有本次合并结果的工作区切到 `main`，安装 [global.json](../global.json) 指定的 SDK，并打开 [TGame.slnx](../TGame.slnx)。Windows 下可用 `dotnet build TGame.slnx` 核对工程加载；仅阅读源码不需要连接外部服务。

实际起服或联调需要 PowerShell 7、SSH 隧道及本机凭据，见 [README](../README.md) 和[服务器连接说明](服务器连接.md)。构建产物、日志和 `.local/secrets/` 不属于 Git 交接内容。

规范以[项目协作规范](../code/agents/项目协作规范.md)为准；持续事项见 [TODO](TODO.md)。
