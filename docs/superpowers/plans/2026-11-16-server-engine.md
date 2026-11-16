# 服务器引擎分层实施计划

> 按用户明确的边界在当前会话执行：进程拓扑归引擎，具体进程的 SinglePoint 留在业务层。

**目标：** 将连接接入、拓扑、连接生命周期和服务发现从宿主提取到独立引擎项目，保持现有运行行为。

**设计：** `app` 引用 `engine` 并编译 `logic`、`model`；引擎仅引用共享协议及通信、日志依赖。引擎 `ServerNode` 提供连接管理，业务 `GameServerNode` 派生类提供消息处理及 SinglePoint 初始化。具体 SinglePoint 保持原有类型与标注。

**技术栈：** .NET 10、现有 gRPC / ZooKeeper / Serilog，不新增第三方依赖。

## 范围与决定

- 用户已确定拓扑归引擎、进程 SinglePoint 归业务；独立引擎程序集是本次实现选择，用编译验证依赖方向。
- 仅移动目录无法阻止反向引用；为现有两个消息处理阶段保留抽象入口，不引入通用插件框架。
- 引擎继续使用当前共享 Frame、ProcessType 和日志协议，不修改线上消息格式。
- Profile、Ping/Pong 处理和对应 PeerLink 扩展位于业务层；app 只保留启动组装及 SinglePoint 注册。
- 服务发现连接地址由宿主传入，避免引擎读取业务 TestStorage。
- 延续 `codex/server-review-snapshot` 当前 Review 事项，开分支 `codex/server-engine-boundary`；按交接约定不整合另一条 main 多实例路线，不合并或推送 main。
- 2026-11-17 按用户要求将本次改动合回 `codex/server-review-snapshot`，后续同一 Review 事项直接在该分支继续。

## 实施与验证

- [x] 增加并运行分层检查，确认现状将引擎与业务编译在一起，检查失败。
- [x] 新建 `code/server/engine/TGame.Server.Engine.csproj`，迁入 LinkService、Topology、ServerNode、PeerLink、ServiceRegistry、TransportProtocol、TraceLog。
- [x] `ServerNode` 不引用具体 SinglePoint；业务派生类处理初始化、消息分发及 Profile/诊断。PeerLink 业务部分改为扩展方法。
- [x] 调整 Program.cs 的对象创建、DI 注册、服务发现参数及项目引用；业务存储代码从 app 迁入 logic，保留配置与存取行为。
- [x] 更新解决方案、目录规范、README、Review 文件链接；同步根目录与 code 的规范入口。
- [x] 构建独立引擎及解决方案，运行 SinglePoint 检查，覆盖握手失败重试、初始化等待、路由授权、断线和取消。
- [x] 运行现有进程联通及控制台验收，确认 DI 创建 LinkService、真实双向流与启动路径正常。
- [x] 审查依赖方向、异常/释放路径及差异，提交本次事项。

## 审查重点

- DI 必须以引擎 ServerNode 类型注册业务节点，确保 gRPC 服务能够构造。
- 请求处理等待嵌套回复时，读取循环仍能唤醒等待者。
- 切分不改变握手、实体归属校验、就绪时机和断线清理。
- 引擎不得编译业务源码或依赖 TGame.Server 宿主程序集。
- 配置仍由既有入口读取，ZooKeeper 地址显式注入，外部协议保持兼容。

## 验证记录

- 分层检查先失败，迁移后通过；独立引擎与完整解决方案构建均为 0 警告、0 错误。
- SinglePoint 测试通过：DBMgr 配置与凭据、GUID 初始化与登记、Profile 路由授权、握手失败重试、WSS/gRPC 就绪门控、取消与断线。
- `Test-PingPong.ps1` 通过：8 个进程、9 条 gRPC 连接、1 条 WSS 会话、2 轮、25 次 Pong、2 次 MongoDB 检查、5 次 GM HTTP 检查。
- `Test-ServerConsole.ps1` 通过：8 实例启动、日志导出、选择性停止、恢复、附加及构建重启；其中故意制造的编译失败属于预期检查。
- 本机已有 SSH 转发占用所需端口；没有替换或停止它，联通验收使用既有转发。
- 独立只读审查未发现新增回归或范围缺失。共享 Frame 的固定消息集合、业务暂时编译进宿主及单实例拓扑是明确保留的现状；本次未承担多实例整合、存储重写或生产容量评估。
