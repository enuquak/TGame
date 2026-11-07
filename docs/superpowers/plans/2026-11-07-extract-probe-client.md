# 拆分探测客户端实施计划

> **For agentic workers:** Use superpowers:executing-plans to implement this plan in the current session; review the complete change before merging.

**Goal:** 服务端入口只启动服务器，探测客户端以独立测试工程运行。

**Architecture:** 新建 `code/tests/probe-client/TGame.ProbeClient.csproj`，不引用服务端工程。双方引用 `code/shared/TGame.Shared.csproj`，复用协议、WebSocket 读帧、Profile 探针数据和 JSON 日志序列化。服务端日志落盘仍由服务端负责，测试客户端仍向 stdout 输出。现有验收入口分别构建并启动两个程序。

**Tech Stack:** 沿用 .NET 10、Google.Protobuf 3.36.2、Grpc 2.85.0、PowerShell 7；不引入新的外部库。

**Spec:** 用户确认上一轮提出的职责划分：服务端入口、独立测试客户端、共享协议，以及协调两边的验收脚本。

## 约束与验收重点

- 保留客户端的本机 WSS 限制、证书指纹验证、帧大小限制、超时和所有回包断言。
- 保留日志字段、协议字段和验收输出，服务端不依赖测试工程。
- 协议只在共享工程生成，控制台的协议导出命令和检查同步改到共享路径。
- 现有源码规范、事项分支提交与合并流程继续适用；不推送远端。
- 结构调整复用现有功能验收；额外核验独立构建、无效参数退出码和客户端运行时依赖，不添加只检查文件布局的测试。

## 实施

- [x] 抽取共享工程，迁移协议生成、Wire、ProfileProbeData、ProbeLog。
- [x] 将 ProbeClient 移至独立测试工程，增加三个参数的入口；移除服务端 client 分支。
- [x] 更新验收脚本、控制台协议导出及检查、README 和目录说明。
- [x] 独立构建客户端及服务端，检查运行时依赖，运行 SinglePoint 测试与控制台验收。
- [x] 通过实际 BAT 入口运行八进程 Ping/Pong 验收，并验证失败退出码。
- [x] 审阅差异、提交事项分支、合并本地 main、恢复测试隧道原始状态。

## 验证记录

- 客户端、服务端、服务器控制台构建均为零警告、零错误。
- 客户端运行时只有 `Microsoft.NETCore.App`，依赖只有自身、共享工程、Google.Protobuf 和 Grpc.Core.Api，不包含服务端、ASP.NET Core 或存储驱动。
- 无参数和多参数时客户端返回 1；非 WSS 地址被拒绝；服务端拒绝旧 `client` 命令。
- SinglePoint 测试通过：握手重试、延迟初始化、WSS/gRPC 就绪门控、取消、断连和未知实体。
- 实际 `Test-PingPong.bat` 返回 0：`PASS processes=8 grpc_links=9 wss_sessions=1 rounds=2 pong=25 mongo_ping=2 gm_http=5`；错误参数经 BAT 返回 1。
- `Test-ServerConsole.ps1` 通过：配置与偏好、滚动日志、共享工程协议导出、八进程启动、选关、恢复、接管及编译重启；隔离工作目录验证从源定义生成协议。
- 控制台验收首次运行在迁移暂存前，git 清单仍包含旧 ProbeClient 路径；暂存迁移后重跑通过，无需修改复制逻辑。
- PowerShell 语法和 `git diff --check` 通过；客户端与共享工程的 IDE0011 检查通过。
- 本轮测试启动的 SSH 隧道已停止，恢复执行前状态。详细输出位于忽略的 `build/extract-probe-client/`。
- 独立审阅覆盖全部 17 个文件、依赖边界及关联调用，未发现需修正问题。
