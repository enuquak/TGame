# RpcChecks.cs 代码详解

源码：[code/tests/engine/RpcChecks.cs](../../../../../code/tests/engine/RpcChecks.cs)

## 职责与两个入口

`CheckTransportBoundary()` 通过程序集反射和 Protobuf 描述符验证分层：Engine 不引用业务 Shared，Shared 也不引用 Engine；传输帧、服务、进程配置、ID 校验和基础日志类型属于 Engine；传输协议的直接依赖不得引入 `tgame.*` 应用协议。它是明确列出的边界检查，不是扫描所有源码依赖的通用架构工具。

`RunAsync(stop)` 对 `PeerLink` 执行内存 RPC 检查。`CreateLink` 使用 Login 类型、Router1 对端、新链路 ID、`FrameStream` 和共享日志构造链路，不需要真实握手或外部服务。

## 请求关联与信封校验

`CheckReplyCorrelationAsync` 同时发出两个期待不同响应类型的请求，拒绝尚未完成的重复请求 ID；不匹配的应答不能完成任何调用；逆序回复必须交给正确调用者。请求完成后相同 ID 可再次使用，此时错误的 `Any` 类型必须抛 `InvalidDataException`。

`CheckInvalidMessagesAsync` 覆盖缺负载、缺 ID、空 `Any` 三种信封。发送和接收都应拒绝，且无效请求不能出现在输出通道中，证明校验发生在真正传输之前。

## 取消、关闭与超时

- `CheckCancellationAndCloseAsync` 在请求发出后取消调用，注入迟到应答，再复用该 ID。随后关闭链路，要求待完成请求和新调用都以 `IOException` 失败。
- `CheckTimeoutAsync` 设置 `25` 毫秒超时，等待请求超时并注入迟到应答，再用同一个 ID 成功调用，检查待完成表已经清理。
- `CheckBlockedWriteTimeoutAsync` 用 `BeforeWrite` 阻塞发送，设置 `75` 毫秒超时，并用 `500` 毫秒保护等待确认超时包含发送阶段。超时请求不能到达输出通道；释放写门闩后再复用 ID，输出必须是本次重试，不能残留已超时的发送。

## 清理与限制

阻塞写场景在 `finally` 取消请求、等待其结束，再打开门闩，避免挂起任务泄漏。其他场景通过完成应答、取消或 `Close` 结束待处理调用。`ThrowsAsync<TException>` 只接受指定异常类型。

这里的毫秒数用于触发有限测试场景，不是生产默认超时或性能指标。内存流不能模拟真实网络背压的所有表现；本文件也不验证应用业务处理或路由。
