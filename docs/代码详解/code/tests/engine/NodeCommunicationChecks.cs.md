# NodeCommunicationChecks.cs 代码详解

源码：[code/tests/engine/NodeCommunicationChecks.cs](../../../../../code/tests/engine/NodeCommunicationChecks.cs)

## 职责与运行方式

`RunAsync(stop)` 顺序验证主动/入站入口的静态检查、节点层直接对端 RPC、入站请求和身份握手失败；可通过独立的 `--node` 模式运行。所有连接由 `FrameStream` 模拟，使用真实 `ServerNode` 和实际连接配置，但不启动 gRPC 监听或 ZooKeeper 会话。`TestServices` 必须已初始化 ID 服务。

## 入口检查必须早于外部操作

`CheckOutgoingValidationAsync` 用反射取得私有 `ConnectAsync` 的委托。传入不带编号的实例名或不允许的主动连接方向，同时使用非法地址，要求先得到 `InvalidArgument`，而不是进入地址解析或网络建连。反射仅存在于测试，不为测试新增生产公开入口。

`CheckIncomingValidationAsync` 在 ZooKeeper 未初始化时调用 `AcceptConnectAsync`，分别给出非法连接 ID、无编号名称和错误方向。都必须返回 `InvalidArgument`，且没有写出 Hello，证明静态检查早于占号查询与握手。

## `CheckPeerCallsAsync`

构造 `Login1` 并与 Router1 完成内存握手，等待当前就绪后调用 `CallPeerAsync<StringValue>`。检查输出请求有规范 ID，`Any` 解包后的负载等于原对象；注入匹配应答后调用者得到字符串结果。

随后检查远端 `Unimplemented` 错误会变成对应 `RpcException`；调用没有连接的 Profile1 返回 `Unavailable`，且 Router 的输出流没有新增帧，证明缺失直接对端时不会偷偷转发给其他进程。预取消令牌必须取消调用。再发送一个待完成请求，关闭输入流并等待链路退出，要求请求收到 `IOException`、节点仍保持首次就绪、后续对同一已断开对端的调用返回 `Unavailable`。

## 入站与错误握手

`CheckIncomingRequestsAsync` 以无主动依赖的 Router1 为接收端，给 Scene1 链路开启 `requireReady: true`。先检查接收端返回自己的 Hello；再连续发送两条合法请求，要求都得到保留请求 ID 的 `Unimplemented`，表明未接业务处理器时明确拒绝。最后发送缺少请求 ID 的非法信封，要求连接以 `InvalidDataException` 终止。

`CheckRejectedHandshakeAsync` 期望 Router1，却喂入 Router2 的 Hello，要求链路抛 `IOException`，并且失败握手不能满足 Login 的首连或当前就绪。

## 辅助断言、清理与边界

`ExpectStatusAsync` 仅接受指定 gRPC 状态，`Check` 处理布尔断言。直接调用场景在 `finally` 取消并等待链路；入站场景最后完成输入通道。测试使用的 `Unimplemented` 正是当前基础框架行为，不是业务成功占位。它不测试真实网络元数据校验、ZooKeeper 身份验证或应用层实体路由。
