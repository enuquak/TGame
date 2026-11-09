# 服务端与联通诊断命名解耦实施计划

> **For agentic workers:** Use superpowers:executing-plans in the current session, then perform one independent whole-change review.

**Goal:** 服务端工程和通用模块使用自身职责命名，Ping/Pong 只作为一种诊断协议存在。

**Architecture:** 服务端改为 `TGame.Server`，节点与注册改为 ServerNode、ServiceRegistry；将链路帧与服务定义移入 transport.proto，诊断消息仍在 pingpong.proto。日志 JSON 和 WebSocket 读帧分别放入共享 logging、transport 目录；测试数据和探针处理集中到 diagnostics。更新所有构建、启动、控制台快照、测试及文档引用。

**Tech Stack:** 沿用 .NET 10、现有 Protobuf/gRPC 和 PowerShell 7，无新依赖。

**Spec:** 用户要求去除工程与 Ping/Pong 测试协议的强命名关联；保留当前联通测试能力。

## 约束

- 保留 Ping/Pong、Profile 消息字段编号、日志事件与字段、鉴权、就绪门控、超时、路由和存储行为。
- 服务端、共享传输协议和脚本需统一重新构建；新的 gRPC 服务名称由同轮进程共同使用。
- 日志系统整合继续保留在 TODO，本轮仅整理其归属和命名。
- 诊断仍是服务器支持的一项功能，不引入可插拔框架或动态加载体系。

## 实施与验证

- [x] 服务端工程、命名空间、通用类型及所有启动引用改为业务中性名称。
- [x] 将连接服务、PeerLink、服务注册、探针处理及共享代码按职责拆分；传输定义与 Ping/Pong 消息定义分开。
- [x] 同步控制台、独立客户端、测试、文档和协议导出路径。
- [x] 构建服务器、客户端和控制台；运行 SinglePoint 测试与控制台隔离验收，证明新名字和干净协议生成可用。
- [x] 通过 BAT 完成八进程联通验收、失败退出码与日志校验。
- [x] 审阅、提交并合并本地 main，恢复测试环境原始状态。

## 验证记录

- 服务端、独立客户端和控制台构建均为零警告、零错误。
- SinglePoint 验收通过：握手重试、延迟初始化、WSS/gRPC 就绪门控、取消、断连和未知实体；新增直接 Profile RPC 检查，验证请求和回包没有 Ping/Pong 包装。
- 实际 `Test-PingPong.bat` 返回 0：`PASS processes=8 grpc_links=9 wss_sessions=1 rounds=2 pong=25 mongo_ping=2 gm_http=5`。
- `Test-ServerConsole.ps1` 在隔离目录通过协议导出、全新服务端构建、八实例启动、选关、恢复、接管和编译重启检查，验证新的 apphost 与快照路径。
- 无参数服务端返回失败并提示 `Usage: TGame.Server`；独立客户端缺少参数返回 1；BAT 错误参数保持退出码 1。
- PowerShell 语法检查通过，独立客户端运行时依赖仍不包含服务端、ASP.NET Core 或存储驱动。
- 代码、工具与当前目录文档中不再引用旧服务端工程或旧通用类型名；历史计划与 Ping/Pong 专用验收入口保留原名。
- 本次启动的 SSH 测试隧道已停止，恢复执行前状态。详细日志保存在忽略的 `build/decouple-server-diagnostics/`。
- IDE0011、空白格式与暂存差异检查通过；最终服务端构建零警告、零错误。
- 独立审阅逐项比对迁移前后的握手、就绪、请求分发、取消、待回包清理、路由、鉴权、协议字段及启动引用，未发现需修正问题。诊断消息仍属于编译时支持集合；同轮统一构建要求已在 README 说明。
