# FrameStream.cs 代码详解

源码：[code/tests/engine/FrameStream.cs](../../../../../code/tests/engine/FrameStream.cs)

## 职责与依赖

`FrameStream` 是测试专用的内存帧流，同时实现 gRPC 的 `IAsyncStreamReader<Frame>` 与 `IAsyncStreamWriter<Frame>`。它把 `PeerLink`、`ServerNode` 所需的双向流接口替换为 `System.Threading.Channels`，使测试无需真实 HTTP/2 连接就能控制握手、请求、应答和断线。

`Input`、`Output` 是两条独立的无界通道。测试写入 `Input` 模拟远端发来的帧，读取 `Output` 观察被测代码发出的帧；两者不会自动互相转发。`Current` 保存最近一次成功读取的帧，在第一次成功 `MoveNext` 前不能当作有效帧使用。`WriteOptions` 仅满足接口，没有模拟 gRPC 的传输选项行为。

## 关键方法

- `WriteAsync(Frame)` 委托给带取消令牌的重载。后者先等待 `BeforeWrite`，再把帧放入 `Output`。测试可以传入未完成任务让发送停在真正入队之前，从而稳定触发发送期间取消或超时。
- `MoveNext(stop)` 先等待 `Input` 可读；通道完成且没有剩余数据时返回 `false`，否则取出一帧并更新 `Current`。等待和读取都响应传入的取消令牌。

## 生命周期与限制

本类不实现释放接口，也不会自行关闭通道。使用者通过完成 `Input.Writer` 模拟 EOF，通过取消令牌结束阻塞的读写，并等待相关链路任务退出。它没有网络缓冲上限、网络延迟或真实连接故障，不能把这些内存测试直接解释为 gRPC 网络可靠性或吞吐量验证。
