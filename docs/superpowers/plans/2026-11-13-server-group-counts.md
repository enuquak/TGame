# 服务器组数量配置 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让节点等待配置中的全部相邻实例，并让启动工具实际启动这些实例。

**Architecture:** 共享 ServerGroup 读取数量配置并生成 ServerProcess 实例；Topology 展开实例连接集合，ServerNode 根据握手成功的实例集合判定就绪。服务发现、日志和启动工具使用相同实例名称及运行组配置快照。

**Tech Stack:** C# / .NET 10、现有 gRPC 与 ZooKeeper、System.Text.Json、PowerShell 7、WPF。

**Spec:** ../specs/2026-11-13-server-group-counts-design.md

## Global Constraints

- 完整花括号、early return、逻辑段落空行；业务标识复用 ProcessType 与常量。
- 不增加第三方依赖。独立工作区从 main 建分支；复用 9e41145 中的 GUID 注册前置改动，不修改原目录的 DBMgr 工作。
- 同组配置固定，数量明确；所有连接必须完成握手，重复或错误实例不能计入。

## Review Focus

- Login 数量足够但 Base 不足：不能用总数冒充齐全。
- 外部连接声称一个未配置的实例：握手前拒绝。
- 最后一条连接握手失败或等待时取消：不能发布初始化成功。
- 控制台恢复已有组时源码数量发生变化：仍用旧组快照识别进程。
- 同类型进程重启：只影响该实例的标记和日志，其他实例保持原 PID。

### Task 1: 配置、实例身份与连接就绪

**Files:** code/shared/configuration/ServerGroup.cs; code/server/config/processes.json; code/server/app/{Topology,ServerNode,LinkService,ServiceRegistry,Program,TraceLog}.cs; code/server/app/diagnostics/ServerNode.*.cs; code/tests/singlepoint/GroupChecks.cs

**Interfaces:** ServerGroup.Load(string path), Processes, GetProcess(string name); ServerProcess(Type, Number), Role, Name; ServerNode(string name, ServerSinglePoint singlePoint, ServerGroup group).

- [x] 增加配置格式与 Gate 等待 2 Login / 3 Base 的失败用例，验证当前实现不满足要求。
- [x] 实现共享配置、实例名称、拓扑展开、注册/握手身份和精确集合就绪检查，更新实体路由授权。
- [x] 运行 SinglePoint 测试，覆盖重复、未配置实例、断线重连、取消和零邻居。

### Task 2: 启动入口与运行快照

**Files:** tools/server-console/{ConsoleSession,ServerInstance,ProcessPage}.cs; tools/server-console/tests/*; tools/scripts/powershell/{Test-PingPong,Read-PingPongLog,Test-PingPongLogs}.ps1; code/shared/logging/LogEventJson.cs

**Interfaces:** 消费 Task 1 的 ServerGroup/ServerProcess；控制台 Instance.Name 唯一，Role 保留类型语义。

- [x] 控制台配置测试先验证对象配置会生成正确数量的实例，再实现启动、恢复、日志与停止标记区分。
- [x] 测试脚本按同一配置展开实例并复制运行快照，日志校验使用运行配置计算期望连接集合。
- [x] 编译服务器、客户端和控制台；运行配置检查及真实多实例启动、缺失实例阻止就绪、单实例重启检查。

### Task 3: 回归、文档与集成

**Files:** README.md; docs/项目文件结构.md; docs/服务器组配置.md

- [x] 说明配置样例、启动命令、就绪语义、0 数量及固定快照。
- [ ] 运行原有联通与控制台测试，保存结果；进行独立代码审查并修复实际问题。
- [x] 核对 diff 和提交范围，提交事项分支。

2026-11-17：将原数量配置实现适配到独立 `server/engine` 项目，复用原有配置与启动器逻辑，补充 Base2 等待两个 Router、Router2 断线恢复的回归测试。脚本、启动器和服务端共用 `ServerGroup` 配置解析。完整验收覆盖 16 个进程、36 条连接，控制台重启后 17 个实例；原工作区有其他 agent 并行重构，本次保留在 `codex/engine-multiple-instances` 分支，不合并 `main`。
