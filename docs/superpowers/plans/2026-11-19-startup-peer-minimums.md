# 启动对端数量门槛 Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans. Steps use checkbox syntax for tracking.

**Goal:** 将 processes.json 数量用于首次启动的对端数量门槛，保留持续发现。
**Architecture:** ServerGroup 提供 GetStartupMinimum(ProcessType)，由 App 注入 ServerNode；FirstLinks 记录一次性达标，Ready 保留连接健康状态。控制台沿用同一数量启动并同步快照。
**Tech Stack:** .NET 10 / C# / gRPC / ZooKeeper。
**Spec:** [设计](../specs/2026-11-19-startup-peer-minimums-design.md)

## 约束与审查重点

- 复用 review-1118；不增加服务接口或第二份数量配置。
- 仅完成握手的主动类型对端计数；0门槛不禁止后续连接。
- 启动阈值只执行一次；失去最后一条连接仍可诊断和恢复。
- 控制台重启/配置变更后，快照数量必须与 App 读取数量一致。
- 仅短本地测试；不启动真实中间件或完整进程组。

## Task 1: 配置、启动门槛与持续发现

- [x] 先扩展 StartupReadinessChecks，测试 Router=2 等待和一次性达标、0门槛与取消；运行并观察失败。
- [x] 增加 ServerGroup.GetStartupMinimum(ProcessType)，通过 App/ServerServices 注入节点；调整握手计数和首次完成条件。
- [x] 将 processes.json 复制到输出；控制台将本轮配置保存到构建快照；补短快照回归。
- [x] 保留并加强 DiscoveryLinkChecks 的后加入、移除、替换及恢复验证。
- [x] 更新 AGENTS 入口、协作规范、架构及操作/阅读文档。
- [x] 编译 engine tests 和 console checks；运行 --engine、--ioc、--discovery-links、--dynamic-topology、控制台 --config-only/--state-only 必要短检。
- [x] 审查 diff，记录验证后提交，不推送。

## 执行记录

- 用户已确认目标类型门槛和控制台复用数量；实施沿用现有当前连接健康语义，不让启动门槛反复生效。
- RED：接入数量配置后，旧计数逻辑在 Router=2 时只连 Router1+DBMgr1 就就绪，新增断言失败；改为首次分类型计数后 --connection-readiness 通过。
- RED：控制台测试构造“编译产物数量1、本轮启动计划数量2/3”，原复制逻辑保留旧数量，断言失败；快照覆盖为本轮计划后 --config-only 通过。
- 最终短检：EngineTests、ServerConsole.Check 编译均0警告0错误；--engine、--ioc、--discovery-links、--dynamic-topology、控制台 --config-only/--state-only 均exit 0。运行时用例包括真实回环gRPC连接第三个Router，无外部中间件。
- 独立审查确认业务路径，通过静态检查发现新增测试在 FirstLinks 通知后立即读取 Ready 的竞态；按 App 流程追加 WaitUntilReadyAsync，补跑 --connection-readiness exit 0，重编译0警告0错误。
- 未运行真实ZooKeeper、数据库及完整控制台进程组测试。
