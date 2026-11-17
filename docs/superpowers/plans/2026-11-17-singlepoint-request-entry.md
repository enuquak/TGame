# SinglePoint 请求入口 Implementation Plan

**Goal:** 移除 ProfileMessages，让目标 SinglePoint 直接处理 RPC。

**Architecture:** 保留通用信封及 Router 实例选择；本地投递调用 SinglePoint 虚方法。Profile 接收只读查询，删除 Router 的业务注册与归属校验。

**Spec:** ../specs/2026-11-17-singlepoint-request-entry-design.md

**Tech Stack:** 现有 C#、.NET 10、Protobuf/gRPC。

## 实施与验证

- [x] 添加无需业务注册即可处理请求的回归检查，确认旧实现失败。
- [x] 在 ServerSinglePoint 增加请求入口，ServerNode 直接投递，删除 RpcDispatcher 的实体处理器和业务校验注册。
- [x] 将 Profile 查询处理迁入 ProfileServerSinglePoint，删除 ProfileMessages/ServerMessages；调用方直接使用 CallSinglePointAsync。
- [x] 同步内存路由、Profile、存储检查代码及现行文档，保留用户已有修改。
- [x] 运行相关编译与短时内存检查，评审后仅提交本次改动。

## 审查重点

- 未知协议仍返回 Unimplemented，默认 SinglePoint 不意外接收业务。
- 随机选实例、原始调用者、请求关联、嵌套调用、取消和断线语义保持。
- Profile 远程写入不会执行存储，参数非法在业务入口拒绝。
- Engine 无业务程序集依赖。

## 验证记录

- RED：新增直接调用检查在旧实现返回 `Unimplemented`，确认缺少 SinglePoint 投递入口。
- GREEN：独立产物目录编译通过（0 警告、0 错误）；`--singlepoint-dispatch`、`--profile`、`--engine`、`--node`、`--routing` 均通过。
- 检查范围为内存连接；未运行真实 MongoDB/Redis 或完整多进程验收。
- 真实存储检查仅同步测试代码并编译，数据准备通过本地存储辅助方法完成；RPC 仅查询并拒绝发布/撤回。
- 保留 Profile 协议历史字段和本地存储辅助方法，本次不实现实体数据修改或资料同步机制。
- 独立只读代码评审：没有发现需要修复的问题。
- 明确的行为调整：Profile 不再按调用进程角色限制查询；调用方直接接收通用 RPC 异常；Profile RPC 拒绝历史发布/撤回操作。实体业务修改和资料同步不在本次范围。
