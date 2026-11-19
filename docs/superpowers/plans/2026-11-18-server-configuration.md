# 进程组配置单例 Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans in the current `review-1118` branch. Preserve concurrent edits and restrict validation to short targeted checks.

**Goal:** 移除进程组配置的逐层传参，统一进程身份查询。

**Architecture:** Engine 的 `ServerConfiguration` 持有 `ServerGroup`，显式初始化、静态调用、显式释放。独立的数据文件读写仍使用 `ServerGroup`。

**Tech Stack:** C# / .NET 10，沿用现有依赖。

**Spec:** `docs/superpowers/specs/2026-11-18-server-configuration-design.md`

## Global Constraints

- 使用现有 `review-1118`，不自动合入 main；不引入依赖、热重载或跨组共享状态。
- 保留配置校验、引擎程序集边界、初始化与释放顺序。
- 仅运行短时检查，完整或外部服务测试须另获授权。

## Review Focus

初始化失败不能发布配置；重复初始化不能替换快照；旧生命周期重复释放不能清除新配置；节点与后台任务结束前配置必须有效；控制台独立读写不受影响。

## Task 1: 配置入口与调用迁移

**Files:** `engine/configuration/ServerConfiguration.cs`；宿主 `Program.cs`；`ServerNode*.cs`、`Topology.cs`、`ZooKeeperProxy.cs`；`tests/singlepoint/` 受影响检查与配置文档。

**Interfaces:** `Initialize(string)` 返回仅管理生命周期的 `ServerConfiguration`；静态 `Processes`、`GetProcess(string)`、`GetProcessType(string)`、`Contains(string)`；`ServerNode(string)`。

- [x] 增加 `ServerConfigurationChecks`，验证加载快照、失败、重复初始化、未知实例、节点构造与释放后重建；编译确认缺失的新入口。
- [x] 实现配置服务，移除调用链的 `group` 参数和节点的 `PeerRole`。
- [x] 按生命周期调整现有测试，保留不同组及多实例场景。
- [x] 编译测试项目，通过 `--configuration`、`--routing`、`--node` 短检查；构建产物隔离到本次检查目录。
- [x] 只读复核未发现具体缺陷，检查差异后在当前分支提交本次改动。
