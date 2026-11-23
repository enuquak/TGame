# DiscoveryLinkChecks.cs 代码详解

源码：[code/tests/engine/DiscoveryLinkChecks.cs](../../../../../code/tests/engine/DiscoveryLinkChecks.cs)

## 职责与测试构成

本文件包含检查入口 `DiscoveryLinkChecks` 和模拟远端服务 `DiscoveryPeer`。测试在回环地址的随机端口启动三个真实 HTTP/2 gRPC 服务，同时使用内存通道提供服务发现快照；因此覆盖真实传输下的连接维护，但不依赖真实 ZooKeeper 发现。

`RunAsync(stop)` 构造 `Scene1`，将 Router 首连门槛设为 `2`，把 `changes.Reader.ReadAllAsync` 交给 `ServerNode.MaintainConnectionsAsync`。三个 `DiscoveredProcess` 分别带身份、实际监听地址和代次，供维护循环比较快照。

## 数据怎样从测试进入引擎

```text
测试写入 changes 通道：一份完整发现列表
    → ReadAllAsync 提供异步序列
    → MaintainConnectionsAsync 对比当前连接任务
    → ConnectAsync 连接本机 DiscoveryPeer
    → Hello 与基础 RPC
    → Handshaken / Closed 通知测试继续
```

`changes` 的元素类型是 `IReadOnlyList<DiscoveredProcess>`。每次写入代表当前完整快照，不是“只增加这一项”的命令。例如先写 `[router1, router2]`，后写 `[router2]`，含义是 Router1 已离开，需要停止其连接任务。

三个 `DiscoveryPeer` 共享测试的 ID 服务，但各有自己的进程名与 WebApplication。`StartAsync` 在 `IPAddress.Loopback` 的端口 `0` 监听，实际可用地址通过 `app.Urls.Single()` 取得；端口 `0` 让操作系统分配端口，避免在测试里假设某个固定端口空闲。

```csharp
var maintenance = node.MaintainConnectionsAsync(
    changes.Reader.ReadAllAsync(lifetime.Token), lifetime.Token);
```

调用立即开始维护，返回的 Task 被保存。此时不能直接 await 它，因为它要持续处理之后写入的多个快照，只有退出或失败才结束。`await using var firstApp = await StartAsync(...)` 则有两个不同的 await 含义：右侧先等应用启动，离开作用域时再异步释放应用。

## 主要场景

1. 只公布 Router1，确认握手后仍不能完成启动；再公布 Router2，等待 `FirstLinks`。
2. 增加 Router3，要求它也会连接，说明首连门槛不是连接上限。
3. 快照缩减到 Router2，等待另两条连接真正关闭，要求首连完成后只剩一条 Router 仍可就绪。
4. 同一 Router2 的 `Generation` 改变，要求旧连接关闭并建立替代连接，覆盖同名进程重启。
5. 提供空快照并等待旧连接关闭，确认仍保持就绪；再只恢复 Router1，确认实际握手和基础 RPC 恢复，不必重新凑齐两台。
6. 以 `InvalidDataException` 结束发现通道，要求维护任务传播失败，并在结束前清理活动连接；通过对已清理实例的调用返回 `Unavailable` 验证清理，不再把 `Ready` 当成当前连接数量。

## 每个等待点都对应一个明确事件

首次写入 `[router1]` 后，测试先从 `first.Connections.Reader` 取得连接对象，再等待 `old.Handshaken.Task`。到这一步，Router1 已与 Scene1 交换过基础 RPC；此时仍未就绪，才能证明“门槛为两台”真的生效，而不是第一条连接还没完成导致暂未就绪。

第二个 Router 加入后，先等它的 `Handshaken`，再等 `node.FirstLinks`。增加第三个 Router 也要等到握手证据，证明维护循环在首次达标后仍处理新增实例，没有把门槛当连接数上限。

移除 Router1 和 Router3 后等待各自 `Closed`，避免只修改发现列表就立刻断言。这里的 `Closed` 由远端服务的 `finally` 完成，提供该条服务调用已经结束的证据。

同名替换通过下面的 C# record 写法构造：

```csharp
changes.Writer.TryWrite([router2 with { Generation = 3 }]);
```

`with` 创建一个新记录，保留原身份和地址，只改变 `Generation`，不修改旧记录。维护层按记录值比较发现对象，代次变化会让旧任务退出并建立新任务；测试据此先等待旧 `extra.Closed`，再读取新的 `replacement` 并等待握手。这里模拟 ZooKeeper 节点重建带来的代次变化，没有实际重启 Router2 的 WebApplication。

空列表 `[]` 表示没有任何可发现目标。测试等替代连接关闭后检查 Ready 仍为 true；再只恢复 Router1 并完成 RPC 握手，证明运行期恢复无需重新达到两台首次门槛。

## 模拟服务的握手证据

`StartAsync` 使用 `WebApplication.CreateSlimBuilder`、`AddGrpc` 和 `MapGrpcService<DiscoveryPeer>`，只监听本机 HTTP/2。`DiscoveryPeer.Connect` 先要求调用方 Hello 为 `Scene1`，返回自己的身份，再主动发送一条带唯一请求 ID 的 `Empty` 请求。收到关联的 `Unimplemented` 应答才完成 `Connection.Handshaken`，因此检查等待的不是单纯 TCP 建连，而是已能交换基础 RPC 的链路。

每条连接都有异步继续执行的 `Handshaken`、`Closed` 完成源，连接对象通过 `Connections` 通道通知测试；`finally` 始终完成 `Closed`，让断线场景可以等待服务端退出。

`DiscoveryPeer.Connect` 可以按四步读：

1. 读取首帧，要求 Hello 为 Scene1；否则抛 `InvalidDataException`。
2. 回复自己的 Hello，再发送一条带新请求 ID 的 `Empty` 请求。
3. 将本次 `Connection` 放入通知通道，让测试得到两个等待信号。
4. 用 `await foreach` 持续读调用方发来的帧，要求每帧是关联该请求 ID 的 `Unimplemented` 回复，才完成 `Handshaken`。

```csharp
internal TaskCompletionSource Handshaken { get; } =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
```

`TaskCompletionSource` 让代码能够主动完成一个可被 await 的任务。`RunContinuationsAsynchronously` 避免在设置信号的当前调用栈里同步执行等待者的后续逻辑；它不表示为每个信号创建专用线程。

两个信号分别表示“确认过基础通信”和“该连接服务调用已退出”。只拿到 `Connections` 中的对象还不能当成握手成功，因为此时请求的响应可能尚未到达。

## 发现失败如何结束整个维护任务

`changes.Writer.TryComplete(new InvalidDataException("Discovery failed."))` 让异步发现序列带错误终止。之后 `await maintenance` 必须抛出该错误；若正常返回，测试主动抛异常指出故障被隐藏。

错误传播之后再调用 `node.CallPeerAsync`，要求得到 gRPC `Unavailable`。这个断言针对连接清理：如果维护任务已经结束却仍留下可调用链接，就是生命周期没有收干净。它不要求 Ready 变为 false，因为 Ready 是已经完成的首次启动状态。

## 清理与限制

外层使用关联取消源管理维护任务，`finally` 取消并等待任务，只接受预期取消或已注入的发现错误。三个 WebApplication 通过 `await using` 释放。此测试没有执行 ZooKeeper 身份占号或发布校验，模拟远端也没有使用正式 `LinkService`，所以其成功不能替代真实发现及启动发布集成检查。

`catch (InvalidDataException) when (changes.Reader.Completion.IsFaulted)` 限定了收尾时可接受的发现异常；不能据此把任意协议错误都吞掉。测试中的 `PASS` 只在所有上述动作和断言成功后输出，阅读到该字符串不代表本轮已经执行或通过了测试。
