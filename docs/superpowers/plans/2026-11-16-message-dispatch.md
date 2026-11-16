# 通用消息分发 Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan in the existing isolated worktree.

**Goal:** 通用分发器不识别 Ping/Pong、Profile、GM 的具体字段和处理规则。

**Architecture:** 启动入口创建消息注册表并注入 ServerNode。各协议模块注册请求处理和回复接收回调；通用分发器只按消息类型查询、将请求加入有界队列、即时分发回复。保留现有业务路由、权限校验、日志和传输格式。

**Tech Stack:** C# / .NET 10 / gRPC / Protobuf，沿用现有控制台测试。

**Spec:** 本次用户要求：通用消息分发器不应知道协议细节，去掉相关逻辑。

## Constraints

- 使用现有隔离分支 codex/remove-test-coupling，不改并行工作区，不合入 main。
- 诊断协议只有显式开启时才注册；未注册协议统一返回 Unimplemented，不解析载荷。
- 不增加动态插件加载，不改变业务路由和权限规则。

## Review focus

- 等待嵌套调用时仍能接收回复；连接关闭和取消仍结束等待。
- 关闭诊断时，请求和回复都不能进入诊断解析器。
- 重复注册不能覆盖已有处理器，未知协议不能回落到 Ping。
- 有界队列溢出仍返回 ResourceExhausted。
- Profile 和 GM 请求及回复的类型、实体与请求 ID 校验保持有效。

## Task: 抽取注册与分发

**Files:** 新增 app/messages/MessageDispatcher.cs、MessageContext.cs、ServerMessages.cs、ProfileMessages.cs、GMMessages.cs；把 diagnostics/ServerNode.Frames.cs 改为独立 DiagnosticMessages；移除 ServerNode.Messages.cs；修改 ServerNode、Program 和单元测试的装配入口；更新目录说明。

- [x] 新增 MessageDispatchChecks：自定义处理器完成同连接嵌套 RPC；未知协议拒绝、重复注册和队列溢出检查。先运行确认缺少注册能力。
- [x] 实现 RegisterRequest / RegisterReply / Receive / DispatchAsync；各协议在模块内注册并处理字段。ServerNode 只传递消息及连接上下文。
- [x] 更新诊断关闭测试，运行单元测试和整个解决方案构建。
- [x] 运行现有 Ping/Pong 全链路测试，覆盖 Profile、GM、诊断日志与路由。
- [x] 独立审查实际修改，修复问题，核对 diff 后提交。

## Execution record

本次是上一项解除测试耦合的后续，继续使用已有隔离分支。业务路由仍保留在各 ServerNode 协议文件中；此次抽离分发层，避免顺带重写路由和连接管理。

- 首次构建新测试因缺少 MessageDispatcher / GMMessages 失败；实现后构建、运行通过。
- `dotnet build TGame.slnx --no-restore`：0 警告、0 错误。
- SinglePointTests：注册分发、同连接嵌套回复、重复注册、有界队列、诊断关闭、Profile/GM 权限路由和既有生命周期测试通过。
- `tools/scripts/Test-PingPong.bat`：8 进程、9 条 gRPC 连接、25 次 Pong、2 次 Mongo 检查、5 项 GM HTTP 检查通过，包含 Profile 的发布、读取、缓存命中与撤回。
- 独立审查未发现需要修复的问题；核对连接关闭、取消、入口校验和协议开关。
- 本次未改控制台界面或持久化配置，未重复运行其完整交互测试；已通过构建与八进程实际启动验收。
