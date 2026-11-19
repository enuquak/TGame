# 连接入口校验 Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 按已确认的职责拆分，使用 `ConnectAsync`、`AcceptConnectAsync` 两个入口完成静态校验，并提供浏览器可查看的 diff。

**Architecture:** 出站在创建 gRPC 调用前校验名称和主动方向；入站入口接管 LinkService 的参数、占号及初始就绪检查。RunLinkAsync 只处理共用握手和连接生命周期，保留锁内动态 Ready 与重复连接检查。

**Tech Stack:** C# / .NET 10 / gRPC；本地 HTML 差异报告。

**Spec:** 本次会话已确认的入口校验方案；接收入口名称由用户指定为 AcceptConnectAsync。

## Global Constraints

- 继续使用 review-1119 分支，保留既有 Program.cs 工作区修改。
- 不修改协议、配置、连接时序和资源所有权。
- 默认只做必要、耗时较短的小范围检查，不运行全套或外部服务测试。
- RunLinkAsync 收为 internal，复用现有 InternalsVisibleTo 内存测试，不新增测试专用生产接口。

## Review Focus

- 出站非法名称或反向拓扑必须在地址解析和网络调用前拒绝。
- 入站非法参数必须在 ZooKeeper 查询及握手前拒绝。
- 等待 Hello 期间失去 Ready 仍须拒绝，不能回复 Hello。
- 重复连接不得提前回复 Hello，也不得清理另一条连接。
- 断开和取消仍须更新就绪状态、结束待处理 RPC。

## Task 1: 拆分入口并交付差异

**Files:** `code/server/engine/ServerNode.cs`、`code/server/engine/LinkService.cs`、`code/tests/engine/NodeCommunicationChecks.cs`、`code/tests/engine/DynamicTopologyChecks.cs`、`code/tests/engine/Program.cs`。

**Interfaces:** 新增 AcceptConnectAsync(peer, id, input, output, stop)；ConnectAsync 保持原有签名；RunLinkAsync 从 public 改为 internal，requireReady 同时明确接收端握手角色。

- [x] 添加一个出站校验顺序的回归检查，并确认旧实现因先解析地址而失败。
- [x] 移动静态检查、简化 LinkService，保留握手后的动态校验；调整内存测试的接收端标记。
- [x] 编译并运行连接、就绪、动态拓扑和本地 gRPC 发现检查，审查 diff。
- [x] 生成可在浏览器查看的本地 HTML 对比，确认页面和 diff 的 HTTP 请求成功；随本次文件提交。

## 执行结果

- 回归检查先观察到 UriFormatException，修改后正确返回 InvalidArgument。
- `dotnet build code/tests/engine/TGame.EngineTests.csproj --no-restore --nologo -v minimal`：0 警告、0 错误。
- `--node`、`--connection-readiness`、`--dynamic-topology`、`--discovery-links`：全部通过，仅运行这些小范围检查。
- 独立只读审查未发现需要修复的问题。
- 报告保存在 `.local/reviews/connect-entry-20261120/index.html`；本地页面和 diff 均返回 HTTP 200。
- 自动打开浏览器受电脑控制工具的网址识别限制而停止，未完成浏览器视觉检查。
