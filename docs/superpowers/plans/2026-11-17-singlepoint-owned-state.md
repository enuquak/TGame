# SinglePoint 自持身份与实体集合 Implementation Plan

> **For agentic workers:** 使用 superpowers:executing-plans 在当前会话执行，完成后做一次独立审查。

**Goal:** 删除 SinglePointState，将 EntityId 与 localEntities 直接放入 ServerSinglePoint。

**Architecture:** ServerSinglePoint 创建时生成身份，业务初始化成功后登记自身；ServerNode 读取该实例的身份与集合完成注册、路由和重连。保留注册确认后才就绪、失败撤销本地登记的行为。

**Tech Stack:** C# / .NET 10，现有 ConcurrentDictionary 与 RPC 注册协议。

**Spec:** 用户在本次代码 Review 中明确要求移除 SinglePointState，并保留 EntityId 与 localEntities 到 singlepoint 中。

## Global Constraints

- 复用当前 review-1117 分支，不合并 main。
- 沿用同一工作区正在完成的静态 IdGenerator.GenId() 入口，不恢复生成器参数传递。
- 集合继续保留；不新增兼容 State 属性或通用实体管理抽象。
- 只执行相关编译、--engine 和 --routing 短时检查，不运行完整测试或外部服务集成测试。

## Review Focus

- 引擎连接完成前不能创建 SinglePoint 或提前分配其身份。
- 业务初始化期间身份已存在，但本地集合和远端注册仍为空。
- 注册未确认时保持未就绪；失败或取消时撤销本地登记。
- 断线后撤销就绪；重连复用原实例、身份和实体集合。
- 无 SinglePoint 时本地路由查询正常返回未找到，注册入口仍拒绝重复身份。

## Task 1: 迁移状态及所有使用方

**Files:** ServerSinglePoint.cs、ServerNode.cs、ServerNode.Entities.cs、ServerNode.Routing.cs；删除 SinglePointState.cs；调整 singlepoint 检查、协作规范与阅读文档。

**Interfaces:** ServerSinglePoint 提供 EntityId、LocalEntityIds、ContainsEntity；InitializeAsync 接收 role、registerEntities、stop。ServerNode 不再公开 State。

- [x] 调整现有初始化和实体注册检查，验证新接口以及业务初始化、注册确认、失败清理的边界，先确认旧实现无法满足。
- [x] 迁移成员与调用方，删除独立状态类型，同步当前规范和 Review 路径。
- [x] 使用 `dotnet build code/tests/singlepoint/TGame.SinglePointTests.csproj --artifacts-path build/singlepoint-state-check --no-restore` 编译，再执行该目录下测试 DLL 的 `--engine` 与 `--routing`，确认退出码为 0。
- [x] 独立审查 diff 和调用链，未发现需要修复的问题。
- [x] 仅提交本项改动；并行日志修改已由 `2c61113` 单独提交。

## 执行记录

- 开始时工作区出现另一个会话的静态 ID 入口改动；先等待其完成，避免混合提交或覆盖同一文件。
- 静态入口改动已由提交 `5dc7535` 完成。本次沿用该入口，之后工作区又出现并行的日志等级修改，保留其内容并分开核对提交范围。
- 新增的引擎构造检查先复现 `SinglePointState` 构造时过早调用生成器的异常；迁移后检查通过。
- 默认输出目录被旧 `--logging` 进程占用，改用 `build/singlepoint-state-check` 隔离产物。首次预期异常留下的本次测试进程已按命令行核实并结束。
- 隔离编译为 0 警告、0 错误；`--engine` 与 `--routing` 均退出 0，覆盖默认类、身份归属、业务初始化前不登记、注册确认/拒绝、重连和路由。未运行完整或外部服务集成测试。
