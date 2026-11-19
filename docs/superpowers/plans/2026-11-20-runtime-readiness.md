# 运行期保持就绪 Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 首次连接门槛完成后，运行期全部对端断开也保持 Ready，继续接受连接和自动重连。

**Architecture:** Ready 从 FirstLinks 的完成状态派生。合并 HasRequiredLinks 与 UpdateLinkReadiness，只在构造及加入握手连接时尝试完成首次门槛，断线只清理连接和待处理 RPC。

**Tech Stack:** C# / .NET 10 / gRPC。

**Spec:** 本次会话已确认的运行期就绪语义；保留首次启动数量、零门槛、取消、身份检查及断线 RPC 错误。

## Global Constraints

- 复用 review-1119 分支；保留已有 ServerNode.cs 声明顺序及 App/Program.cs 工作区修改。
- 同步根入口、源码入口、规范正文及直接相关说明。
- 仅运行连接、启动就绪、动态拓扑、本地 gRPC 发现检查，不运行全套或外部服务测试。

## Review Focus

- 首次达标前仍等待全部必需类型及数量，未握手连接不计数。
- 全部依赖断开后 Ready 不回退，入站握手仍然接受。
- 断线后未连接 RPC 返回 Unavailable，待处理请求失败；对端恢复后重新连通。
- 发现结束必须清理连接，不能再用 Ready 为假作为清理证据。

## Task 1: 收敛首次就绪与更新验证

- [x] 修改已有就绪回归检查，确认原实现不符合新语义。
- [x] 合并首次门槛检查，删除重复状态和运行期就绪回退。
- [x] 更新关联测试及规范，运行必要检查并审查差异。
- [x] 准备本次提交范围和浏览器 diff。

## 验证结果

- 旧实现明确失败于“全部 Router 断开后应保持首次就绪”的断言。
- 编译成功，0 警告、0 错误；`--connection-readiness`、`--node`、`--dynamic-topology`、`--discovery-links` 均通过。
- 独立只读审查未发现阻塞问题；未运行全套或外部 ZooKeeper 测试。
- 本次差异页保存在 `.local/reviews/runtime-readiness-20261120/index.html`，本地 HTTP 页面返回 200；未进行浏览器视觉检查。
