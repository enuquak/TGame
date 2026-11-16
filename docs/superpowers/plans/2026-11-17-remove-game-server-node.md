# Remove GameServerNode Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for this migration.

**Goal:** 统一使用 ServerNode，Profile 接入通用实体 RPC，保持现有业务结果与校验。

**Architecture:** ProfileMessages 保留调用端的错误转换、响应校验和 Router 写入归属校验；ProfileServerSinglePoint 执行存储请求。引擎只提供按消息类型注册的转发前校验、当前接入进程信息和实体归属查询，不引用 Profile。

**Tech Stack:** C# / .NET 10 / Protobuf / existing RPC transport.

**Spec:** 本会话已批准的迁移方案；通用路由见 ../specs/2026-11-17-entity-rpc-design.md。

## Constraints

- 在当前 codex/server-review-snapshot 分支工作，不扩展拓扑、不新增协议或依赖。
- 保留参数、来源和写入归属校验；保留 ProfileReply 的业务 ID、实体 ID、Rejected / Unavailable 语义。
- MongoDB / Redis 存储逻辑不变；只运行无需外部服务的小范围检查。

## Task 1: Migrate Profile and remove the business node

- [x] 先添加通用 RPC 上的 Profile 路由回归检查，确认旧实现不能保留写入归属校验。
- [x] RpcDispatcher.RegisterEntity 增加可选的同步转发前校验，返回业务拒绝响应或继续；EntityRpcContext 提供本跳来源；ServerNode 提供只读进程类型和归属查询。
- [x] ProfileMessages 注册通用处理器与 Router 校验，调用端使用 CallSinglePointAsync；ProfileServerSinglePoint 接管存储调用和异常处理。
- [x] 删除 GameServerNode 及 PeerLink.Profile，调整宿主、业务程序集扫描入口、测试与现有阅读文档。
- [x] 编译并运行 Profile、节点生命周期、通用路由和消息分发的小范围检查；独立编译引擎。
- [x] 复核 diff 与依赖边界，提交本事项改动。

## Review focus

- 非所有者 publish / withdraw 必须在 Router 拒绝，不到达存储。
- Profile 服务端不能通过本地直调绕过 Router 校验。
- 业务 request_id 与通用 RPC request_id 分离后，回包仍准确关联。
- 未初始化、断线和取消保留原有调用端结果或取消行为。
- 宿主继续扫描业务程序集，不能误用引擎程序集导致业务 SinglePoint 缺失。
