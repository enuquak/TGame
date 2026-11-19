# 移除 SinglePoint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将当前服务器收敛为用户选定的进程基础框架。

**Architecture:** 删除 SinglePoint 和实体路由，保留进程身份、发现与连接。就绪条件仅依赖主动连接；基础 RPC 直接面向已连接的进程，不安装业务处理器。

**Tech Stack:** .NET 10、gRPC/Protobuf、ZooKeeper、Serilog、PowerShell 7。

**Spec:** `docs/superpowers/specs/2026-11-18-remove-singlepoint-design.md`

## Global Constraints

- 继续使用 `review-1118`；只提交本次文件，不自动合入 main。
- Engine 不引用业务 Shared 或宿主。进程名通过 `ServerProcess` 生成，业务标识引用已有常量。
- 生成协议通过正常构建更新，不手改生成文件。
- 只运行短时定向检查，不启动完整联调或真实服务器组。

## Review Focus

- 无依赖的 Router 不应等待入站连接而阻塞启动。
- 任一必需类型最后一条连接断开后必须撤销就绪；重连可恢复。
- 未支持的请求必须返回明确错误，响应仍按请求 ID 匹配。
- 控制台不得继续依赖被删除的 SinglePoint 事件或数据库初始化。
- 测试、脚本和当前规范不得保留失效的项目路径或启动要求。

### Task 1: 引擎与宿主

**Files:** `code/server/engine/ServerNode*.cs`、`protocols/transport.proto`、`logging/ServerLogProtocol.cs`、`code/server/app/Program.cs`，删除 SinglePoint 类与业务子类。

**Interfaces:** 保留 `Ready`、`FirstLinks`、`WaitUntilReadyAsync`、`RunLinkAsync`；删除 `IDisposable` 空生命周期。增加 `CallPeerAsync<TReply>(string peer, IMessage request, CancellationToken stop)`；新增 `ServerLogEvents.ProcessReady = "PROCESS_READY"`。

- [x] 增加仅靠握手就绪的行为检查，并确认旧设计失败；直接 RPC 检查随迁移补齐。
- [x] 删除实体注册、路由和业务初始化；简化协议与连接工作循环。
- [x] 调整宿主启动、就绪事件和测试程序集引用。
- [x] 编译并运行对应基础框架检查。

### Task 2: 测试、控制台和操作入口

**Files:** `code/tests/singlepoint/` 移至 `code/tests/engine/`，项目改为 `TGame.EngineTests.csproj`；`tools/server-console/`、相关 PowerShell 检查入口。

**Interfaces:** 使用 Task 1 的进程就绪条件、直接 RPC 与 `ProcessReady`；保留存储工具的独立检查，删除 SinglePoint/实体业务检查。

- [x] 替换依赖 SinglePoint 的测试夹具，保留连接、重连、取消和 RPC 错误检查。
- [x] 控制台消费进程就绪事件，更新日志夹具和检查脚本。
- [x] 编译测试与控制台，运行短时本地检查。

### Task 3: 规范与集成

**Files:** 根和源码 `AGENTS.md`、`code/agents/`、当前结构/启动/review 文档。

- [x] 将 SinglePoint 强制约定替换为进程框架约定，维护测试路径和操作命令。
- [x] 检索当前代码和工具中的遗留引用，审查实际 diff。
- [x] 记录定向验证结果，提交到 `review-1118`。

## 执行记录

- 按用户已选定范围直接执行；引擎由主代理负责，测试、控制台和文档按文件边界并行处理，构建窗口串行分配。
- 旧实现 `--connection-readiness` 检查失败：Router 无主动依赖仍因业务初始化门槛而未就绪。
- 修改后的服务端 `dotnet build code/server/app/TGame.Server.csproj --no-restore --verbosity minimal` 通过，0 警告、0 错误。
- 新测试项目 `--engine`、`--dynamic-topology`、`--discovery-links`、`--logging`、`--configuration` 通过；控制台 `--state-only` 通过，后者也完成旧事件映射的红绿验证。PowerShell 检查入口语法检查通过。
- 独立审查发现直连 RPC 写入阶段不受原响应超时保护：新增 75ms 阻塞写入检查，旧实现由 500ms 守卫捕获失败；修复后 `--rpc` 通过，验证请求 ID 释放与后续写入恢复。超时统一覆盖排队、写入和响应。
- 基础 YAML 只有 `zooKeeper` 和 `discoveryRoot` 时，旧宿主校验报错；将存储配置校验后移至 `ProfileStore.OpenAsync` 后，`--engine` 再次通过，并验证独立存储入口仍拒绝缺项。
- 根 README 的旧命令与启动说明同步更新。ERROR 最低级别隐藏 INFO 就绪事件属于既有已记录限制，本次保留。
- 未运行真实服务器组、ZooKeeper/数据库外部联调或完整测试；当前检查中故意构造的断线与未匹配响应会输出相应 ERROR 日志，断言均通过。
