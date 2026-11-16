# Entity RPC Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans for inline implementation and a final independent review.

**Goal:** 打通 CallEntityAsync、CallSinglePointAsync 的引擎寻址、逐跳转发及回包。

**Architecture:** 扩展通用 RPC 信封；PeerLink 复用请求等待并支持原始信封转发。ServerNode 维护显式 SinglePoint 登记及类型索引，Router 随机选目标，实体处理器返回响应，由引擎负责回包。

**Tech Stack:** .NET 10、C#、Protobuf、gRPC、现有内存流检查。

**Spec:** `docs/superpowers/specs/2026-11-17-entity-rpc-design.md`

## Global Constraints

- 直接复用当前非 main 分支；引擎不引用具体业务；生成协议只由构建生成。
- ProcessType 与 ServerProcess.FormatName 复用现有声明；保留两阶段初始化。
- 只做小范围验证；Profile 内部业务逻辑不变。

## Review Focus

- Gate 经 Login 转发时原始来源丢失或可伪造。
- Router 转发等待阻塞嵌套 RPC；路由错误关闭整条连接。
- 多实例选择串包，断线后仍选中失效实体。
- SinglePoint 已定位后找不到实体造成循环。
- 取消、无候选、未就绪和未知处理器不能及时结束请求。

### Task 1: 实体路由与回包

**Files:** `shared/protocols/transport.proto`；`server/engine/{PeerLink,RpcDispatcher,ServerNode,ServerNode.Routing,EntityRpcContext}.cs`；`tests/singlepoint/{EntityRoutingChecks,Program}.cs`（均位于 code 下）。

**Interfaces:** 两个公开调用方法见设计；`RpcDispatcher.RegisterEntity<TRequest,TReply>(Func<EntityRpcContext,TRequest,CancellationToken,Task<TReply>>)`；上下文包含 RequestId、SourceProcess、EntityId。

- [x] 编写内存双向流测试：本地/直连/Gate→Login→Router→Profile、按类型选实例、嵌套调用、错误与断线清理；首次检查因两个公开接口缺失而失败。
- [x] 扩展信封与显式 SinglePoint 握手；实现调用、索引、有限并发转发及逐跳错误回传。
- [x] `--routing` 通过，含原有多实例连接与归属检查。
- [x] 既有 `--engine` 检查及独立引擎编译通过，0 警告、0 错误。

### Task 2: 文档、审查与提交

**Files:** 根/源码 AGENTS、项目协作规范、项目文件结构与本计划。

- [x] 同步分支规则及引擎 RPC 使用方式，记录已批准的范围。
- [x] 独立审查路由和连通性边界；补齐请求级截止、身份冲突校验，并按用户要求收回额外路径。
- [x] 核对 `git status`、`git diff --check`、`git diff --cached`，只提交当前事项；保留在当前 review 分支。

## 执行记录

- 现有 `--engine` 基线通过（约 8 秒）。用户明确指定当前 review 分支开发，覆盖从 main 另建事项分支的原规则。
- Ruling: 用户要求避免过度处理，Gate 不会经 Base 调用 DBMgr；撤销该场景及 Router 间新增连接，保留原有拓扑。Router 之间的实体调用本轮不扩展。
- 审查：拒绝与 Router 本地实体冲突的登记（失败测试已复现，修复后通过）；接收端请求级截止和处理器取消已验证；Gate 中转遵循本地命中或交 Router 的通用规则。
- 并行会话正在清理 GM/诊断，保留其修改。默认构建目录一度被失败测试子进程占用，已确认并清理本事项遗留子进程；后续构建使用 `.local/entity-rpc-build`。
- 验证命令：`dotnet build code/tests/singlepoint/TGame.SinglePointTests.csproj -p:ArtifactsPath=<仓库>/.local/entity-rpc-build --no-restore`；对该目录的 TGame.SinglePointTests.dll 分别运行 `--routing`、`--engine`；独立编译 TGame.Server.Engine.csproj。均通过，不运行外部服务或完整集群测试。
