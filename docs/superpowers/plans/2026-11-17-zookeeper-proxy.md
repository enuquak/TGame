# ZooKeeper Proxy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将进程注册、服务发现和 worker 分配的 ZooKeeper 操作集中到一个进程单例。

**Architecture:** ZooKeeperProxy 持有客户端和会话，提供实例操作并通过 Instance 访问。宿主显式初始化、监督并释放代理；生成器持有申请到的 worker 租约和会话状态，算法不直接操作 SDK。

**Tech Stack:** 现有 .NET 10、ZooKeeperNetEx、控制台检查项目，不新增依赖。

**Spec:** `docs/superpowers/specs/2026-11-17-zookeeper-proxy-design.md`

## Global Constraints

- 在 `review-1117` 继续，保留宿主已有修改，不合并 main。
- Engine 不引用宿主或 Shared；连接参数从配置读取；中文注释、完整花括号。
- 仅小范围本地验证，不运行外部服务或完整流程。

## Review Focus

- 会话断开、过期和认证失败：旧生成器永久拒绝发号，宿主获知故障。
- 初始化取消或失败：不发布单例，不遗留无人管理的连接。
- worker 申请取消：未能确认的临时节点不能遗留在继续使用的共享会话中。
- worker 释放与代理关闭：不误关其他使用者的连接，关闭代理后旧生成器立即失效。
- 进程注册冲突与等待发现：保持节点所有权、地址校验、取消等待语义。

## Task 1: 统一代理和所有使用方

**Files:** 新建 Engine/ZooKeeperProxy.cs、ZooKeeperOptions.cs、ZooKeeperSession.cs、config/zookeeper.json；删除 ServiceRegistry.cs；修改 SnowflakeIdGenerator、IdGeneratorOptions、ServerNode、宿主 Program.cs 与项目文件、相关 singlepoint 检查、雪花ID和项目文件结构文档。

**Interfaces:** `ZooKeeperProxy.InitializeAsync(ZooKeeperOptions, CancellationToken)`、`Instance`、`SessionLost`、`RegisterProcessAsync(ServerGroup, ServerProcess, string, CancellationToken)`、`ResolveProcessAsync(ServerProcess, CancellationToken)`、内部 worker 租约申请与释放；`ServerNode.MaintainLinkAsync(string, CancellationToken)`。

- [x] 在现有 ID 生命周期检查中断开后调用 GenId，确认失败。
- [x] 实现统一代理、会话和配置，迁移调用方；保留进程临时节点路径与保护逻辑。
- [x] 更新本地会话检查与真实 ZK 检查入口，覆盖失效后不恢复、共享会话的租约释放和进程发现。
- [x] 编译 SinglePointTests 并运行 `--zookeeper-session`、`--engine` 小范围检查，核对通过输出。
- [x] 同步文档，审查改动并提交本事项，保留用户原有修改在工作区。

## 执行记录

- 用户已选择直接提供所需操作的代理方案；不再为同一方案重复请求批准。
- RED：新增断线后 GenId 拒绝检查，原实现未抛 InvalidOperationException；迁移后通过。
- 编译 0 警告、0 错误；本地 `--zookeeper-session`、`--engine` 检查通过，未运行真实 ZK、数据库或完整测试。
- 独立审查未发现生产阻断问题；修复了真实 ZK 检查中先创建辅助客户端、初始化失败时遗漏关闭的清理顺序。
- worker 复用的下一毫秒等待一并放入代理申请超时内，保留原有时钟异常时的等待上限。
- Program.cs 仅暂存本事项的调用与生命周期修改，原有辅助函数提取保留未提交。
