# Singleton Methods Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for implementation; use superpowers:dispatching-parallel-agents for the independent read-only service audit.

**Goal:** 清理进程单例中的重复步骤和职责混杂，保留运行行为。
**Architecture:** 类内私有方法提取与既有方法复用，服务边界和 IoC 注册不变。
**Tech Stack:** C# / .NET 10 / ZooKeeperNetEx / gRPC。
**Spec:** ../specs/2026-11-18-singleton-methods-design.md

## Global Constraints

- 继续 review-1118，不推送、不合并；保留初始化保护、租约清理和 Watch 行为。
- 遵循状态与派生属性规范，不删除独立生命周期状态，不增加通用执行器。
- 仅短范围本地验证；真实 ZK/数据库及完整进程组未获授权，不运行。

## Review Focus

- 节点占用之外的异常必须传播到原有会话关闭路径。
- 发现空地址与无效节点时仍保留数据 Watch。
- 双向通信一侧结束后的取消、队列完成、等待及异常传播保持顺序。
- 会话锁、重复连接拒绝、当前就绪变化与首次就绪保持语义。
- 属性改为派生后保持规范身份；缓存与生命周期状态不误删。

## Tasks

- [x] 1. 检查调用方与现有测试；执行短本地基线，完成 ZK 三项提取。
- [x] 2. 根据独立检查整理其他值得修改的服务，记录保留项及理由。
- [x] 3. 运行直接相关本地检查，审查实际 diff，同步必要文档并提交。

## Execution record

- 初始工作区干净，基线提交 79d1373。本次为行为保持的结构重构，使用既有行为检查作为回归依据。
- 独立检查确认：Node 收发循环、配置无环校验、App 身份文件写入具有明确职责边界，已提取；Node 不再重复存储身份派生数据和目标类型列表引用。构造时仍校验实例编号。
- ID 与日志已分别集中生成/输出逻辑，保留其生命周期锁和失败回收；发现 worker 的逐个替换与整体并行退出保留不同处理方式；不增加通用生命周期或配置解析抽象。
- EngineTests（含 App/Engine）构建通过，0 警告、0 错误。重构前后 `--engine` 均通过；重构后 `--ioc`、`--discovery-links`、`--dynamic-topology` 均退出 0（每项约 1–3 秒）。
- 真实 ZK 节点抢占及 Watch 联调未运行；相关代码按原异常边界与调用次序提取，现有集成测试已编译。未运行数据库或完整进程组测试。
- 独立审查通过，未发现需修复项；确认抢占异常传播、Watch 注册、传输取消/等待顺序、锁与清理边界保持。worker 复用路径创建方法后，每级目录创建前也执行既有会话检查，失效时更早进入原清理路径。
- 阅读路径已同步新方法边界，`git diff --check` 通过。
