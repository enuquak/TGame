# Process IoC Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for the integrated implementation and superpowers:dispatching-parallel-agents for independent test/document migrations.

**Goal:** 进程服务改为 IoC 单例与显式依赖，保留通信和启动行为。
**Architecture:** App 组装内置 DI 容器，Engine 接收具体依赖；异步初始化与认领顺序由 App 控制。日志作为提前创建的唯一实例，由 App 在容器释放后清理。
**Tech Stack:** C# / .NET / Microsoft.Extensions.DependencyInjection / ZooKeeper / gRPC / Serilog。
**Spec:** ../specs/2026-11-18-process-ioc-design.md

## Global Constraints

- 复用 review-1118；不合并 main，不推送。
- Engine 不引用业务 Shared/App；不恢复 SinglePoint/业务入口。
- 保留进程编号、日志协议、ID 租约、连接验证、失败清理、取消及就绪语义。
- 只运行短范围本地检查，不运行真实 ZK/数据库或完整进程组测试。

## Review Focus

- 构建容器及配置失败仍有日志，日志最终释放。
- 异步初始化失败、预取消及释放后的对象不可使用，不泄漏连接或租约。
- 多个容器/测试实例不通过静态状态相互污染。
- 单根容器 ID/ZK 释放顺序、后台停止及认领前解析身份。
- 独立存储工具和外部集成测试调用已迁移但不在默认启动中运行。

## Tasks

- [x] 1. 迁移引擎服务与配置：按 Spec 的构造函数/实例 API，实现无全局状态的服务；同步 Node、PeerLink、LinkService 和存储工具。
- [x] 2. 迁移 App 注册与启动：构建单根容器，显式连接/认领/ID 初始化，保留失败日志及逆序释放。
- [x] 3. 迁移测试并加入 IoC 隔离/共享/释放检查；先记录旧结构缺少实例 API 的失败，再验证目标行为。
- [x] 4. 更新规范与当前架构文档，检查残留静态入口；运行上述短检查、独立审查，提交本地分支。

## Execution record

- Scope authorized by user's acceptance of the inventory; implementation details above preserve that scope.
- Pre-flight: tests and docs consume the exact APIs in the spec; implementation owns production files, test migration owns code/tests/engine, documentation migration owns current docs and AGENTS entries.
- RED：旧实现下两个 ServiceProvider 分别解析日志，第二次创建抛出 `The process logger is already initialized`，`--ioc` 退出 1。
- GREEN：App、EngineTests 构建通过，0 警告、0 错误；`--ioc`、`--engine`、`--logging`、`--dynamic-topology`、`--discovery-links` 均通过，每个检查约 1–2 秒。
- 实际 App 子进程检查：未设置配置、ZK 端口格式非法，均退出 1 且输出 `PROCESS_FAILED`；未连接真实 ZK。
- 静态入口残留检查与 diff 格式检查通过，当前文档本地链接无缺失。真实 ZK、数据库和完整进程组测试未运行。
- 适配说明：使用独立 `ServerServices` 注册入口供 App 和 IoC 检查共用；Serilog 自诊断开关移到宿主，避免不同日志实例互相关闭全局诊断。
- 独立审查发现两处相邻初始化保护问题，均已修复：提前认领不再关闭未初始化实例；连接成功且通过超时检查后才公开客户端。两项回归均记录 RED→GREEN；等待握手的检查只使用本地临时 TCP 监听，不连接真实 ZooKeeper。
- 修复后 `--ioc` 与 `--engine` 退出 0，旧静态入口搜索无残留，`git diff --check` 通过。审查无其他阻塞项，无延后发现。
