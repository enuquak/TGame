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
- [x] 运行原有联通与控制台测试，保存结果；进行独立代码审查并修复实际问题。
- [x] 核对 diff 和提交范围，提交事项分支，验证后合并本地 main，不推送。

## 验证记录

- 原有基线 SinglePoint 测试通过；配置格式测试及 Gate 仅连 1 Login / 1 Base 的回归用例先失败，完成实现后通过。
- SinglePoint：2 Login / 3 Base、重复/错误/越界实例、错误握手、取消、断线重连、零邻居、Base2 所有权隔离、Login2 到 Base3 的实体路由全部通过。
- BAT 联通验收：默认 8 进程 / 9 gRPC 连接；多实例 17 进程 / 42 连接。两组均通过 25 次 Pong、Profile 与 5 个 GM HTTP 用例。
- 控制台：17 实例的 Base3 单独停止/补起、重新接管、凭据失败恢复、编译失败不影响原组；编辑配置后新组增加为 18 实例并重新接管通过。
- 运行快照恢复、旧版单实例运行组迁移、非法配置、BAT 失败退出码 1、PowerShell 语法检查通过；控制台 8/18 页签加载检查通过。
- 独立审查发现历史 12/13-case 日志复核回归：新增兼容测试复现两个失败，修复实例清单推导后通过；当前 8/17 实例日志复核再次通过。
- 审查范围裁定：业务请求仍选首实例，运行中不热更新组成员；大规模部署容量未作保证。该事项验证规模为 17–18 个本机进程。
- 完整执行日志保存在本事项工作区的 `build/`，运行验收材料在 `logs/tools/pingpong/`；这些产物不提交 Git。
