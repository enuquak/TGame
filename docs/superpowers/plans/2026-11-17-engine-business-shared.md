# Engine 与业务 Shared 分离 Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan in the current session.

**Goal:** Engine 不引用业务 Shared，保留现有进程角色、协议格式和运行行为。

**Architecture:** 将通用通信协议、进程配置、ID 校验、通用日志与运行标记收回 Engine。Shared 保留 Profile 协议、业务日志事件与宿主业务环境变量；宿主和控制台显式引用二者，不新增项目。

**Tech Stack:** .NET 10、C#、Protobuf/gRPC、PowerShell 7。

**Spec:** 本次会话中用户确认的第一种方案，以及“留在当前 review-1117（不新建分支）”。

## Global Constraints

- 在当前 `review-1117` 修改并提交，不新建分支、不自动合入 main。
- 保留进程枚举、编号命名规则、协议包名与字段号、日志值和环境变量值。
- 保留现有类型命名空间，避免无关调用方改名；混合定义按职责拆分。
- 不执行完整测试或依赖真实基础服务的检查，只做短时相关验证。

## Review Focus

- Engine 的编译与运行依赖中不能残留 Shared；Shared 不反向引用服务器引擎。
- 协议生成归属准确，旧生成文件不被默认 Compile 重复纳入。
- 控制台导出同时生成引擎和业务协议，并继续传播生成失败。
- PowerShell 从迁移后的唯一声明加载类型，重复导入和实例编号仍正确。
- 日志、凭据变量和运行文件名的值不变，文档链接与目录说明一致。

## Task 1: 分离程序集与工具依赖

**Files:** `code/server/engine/{configuration,identifiers,logging,protocols}/`、`code/shared/{csharp,protocols}/`、Engine/Shared/App/控制台 csproj、`RpcChecks.cs`、`ConsoleSession.cs`、控制台检查入口、`Import-ServerTypes.ps1`。

- [x] 扩展 `RpcChecks.CheckTransportBoundary`，先运行 `--engine`，确认因 Engine 引用 Shared 失败。
- [x] 迁移通用类型与 transport.proto，拆分 ProfileLogEvents、EngineEnvironment 和 EnginePeerNames，更新使用方与项目依赖。
- [x] 控制台顺序导出两套协议；脚本加载引擎基础声明及业务环境变量。
- [x] 编译独立 Engine、SinglePointTests 和控制台检查项目；运行 `--engine`、`--routing`、`--profile` 及隔离目录中的协议导出检查；验证 PowerShell 类型加载和实例命名。

## Task 2: 同步规范与 Review 路径

**Files:** 根目录与 code 的 `AGENTS.md`、`code/agents/项目协作规范.md`、`docs/项目文件结构.md`、`docs/代码Review路径.md`、`README.md`。

- [x] 更新 Shared 业务定义、依赖方向、协议生成目录、工具加载路径及 Review 阅读顺序。
- [x] 检查 diff、失效链接与旧路径，使用 Ponytail review 检查实际变更；核对范围后提交。

## Execution Record

- 用户已明确授权直接实施；不再增加方案或计划确认步骤。
- 当前工作区起始状态干净，分支 `review-1117`，基线 `7b5e09a`。
- 边界检查先以 `Engine must not reference business Shared.` 失败，迁移后 `--engine` 通过；该次预期失败在 Windows 留下的测试进程已按 PID 和命令行核对后停止，随后正常编译运行。
- 独立 Engine 和控制台检查项目编译均为零警告、零错误；SinglePointTests 编译并运行 `--engine` 成功，`--routing` 和 `--profile` 均通过。
- 无生成文件的隔离副本执行控制台 `--export-only` 通过，同时通过配置与日志检查；未启动服务器或连接真实基础服务。
- PowerShell 重复导入和服务器组读取通过；六份直接迁移的源文件内容一致，拆分的日志与环境变量常量值全部保留。
- 当前文档链接、Engine 无项目引用及运行依赖清单无 Shared 检查通过。未运行完整测试、真实 MongoDB/Redis/ZooKeeper 集成或整组起停测试。
- 独立审阅未发现具体回归或待修复问题；确认保留现有进程角色耦合和既有热重载范围，不扩大此次改动。
