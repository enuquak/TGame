# PeerLink.cs 代码详解

源码：[code/server/engine/PeerLink.cs](../../../../../code/server/engine/PeerLink.cs)

## 职责与状态

`PeerLink` 是一条进程双向流的本地管理对象，主要负责串行写入和 RPC 请求/回复匹配。`RunLinkAsync` 在等待 Hello 前构造它，握手完成后才登记为可调用连接。它接收本端角色、对端实例名、连接 ID、流输出端及共享日志，不拥有 gRPC channel。连接的创建、读循环和结束由 [ServerNode.cs](ServerNode.cs.md) 管理。

`waiting` 以请求 ID 索引 `TaskCompletionSource<RpcReply>`；`pendingChanges` 保护该字典与 `closed`；`writer` 信号量保证多个调用者不会同时写同一 gRPC 输出流。这两种同步分别保护“待回复状态”和“网络写入”，不互相替代。

## 发起一次调用

泛型 `CallAsync<TReply>` 把 `IMessage` 包为 `Any`，生成请求信封交给内部重载，收到结果后再按 `TReply` 解包。

内部 `CallAsync` 的顺序是先校验请求 ID 与类型化 payload，检查调用取消，创建关联取消源，再给该调用设置超时。默认超时为 12 秒，可由内部调用参数覆盖。它在锁内拒绝已关闭连接及重复请求 ID，并先登记等待槽，再写请求帧，防止快速回复到达时找不到等待者。

`waiting.TryAdd(requestId, reply)` 就是进入等待字典的位置，发生在 `WriteAsync(new Frame { Request = request }, ...)` 之前。value 是等待响应的 `TaskCompletionSource<RpcReply>`，不是待发送的 Request。请求 ID 由 `ServerNode.CallPeerAsync` 生成并传入；构造 PeerLink 时收到的 id 是连接 ID，主要用于关联日志。

一次 A 调用 B 的完整路径：

```text
A：CallPeerAsync → CallAsync → 加入 A 的 waiting → 写出 Request
B：ReadAsync → 本地请求队列 → ServeAsync → 校验请求并回复 Reply
A：ReadAsync → link.Receive → 按 RequestId 完成等待任务
A：CallAsync 的 finally 移除 waiting → ReadReply<TReply> 解包或抛出错误
```

因此，B 的 `ServeAsync` 不为这次回复添加 waiting；只有 B 自己再主动向 A 发起另一条请求时，才会登记到 B 的 waiting。两端都具有这两种能力，不能按“客户端/服务端”把 PeerLink 固定为只能请求或只能回复。

写入与等待回复共用 deadline token，所以排队等待写锁和写入耗时也计入总超时。只有内部 deadline 到期且外部调用 token 未取消时，`OperationCanceledException` 才转换成 `TimeoutException`；用户主动取消仍保留取消语义。无论成功、失败还是取消，`finally` 都删除该请求的槽位。

## 接收回复与解包

`Receive` 先做协议边界校验。错误回复必须有请求 ID、没有 payload，状态码必须是已定义且非 OK 的 gRPC 值；成功回复必须有非空请求 ID 和带类型 URL 的 payload。

随后按请求 ID 查找等待者并完成其任务。找不到时记录 `UNMATCHED_REPLY` 后返回，常见原因是请求已经超时并释放槽位；不会让这个迟到回复破坏共享连接。`RunContinuationsAsynchronously` 避免完成任务时同步执行调用方续体。

`ReadReply<TReply>` 将错误信封转为 `RpcException`；成功结果先比较 Protobuf 描述符，再 `Unpack<TReply>`。错误消息类型直接抛出 `InvalidDataException`，不尝试强行转换。该方法依赖正常接收路径已经检查过信封结构。

## 关闭和错误边界

`WriteAsync` 无论写入成功还是异常都释放写锁；取消在等锁时生效。`Close` 在状态锁内设置关闭标记，并以 `IOException` 结束全部未完成调用。各调用随后由自己的 `finally` 移除字典项；重复关闭不会把成功完成的任务改写成错误。

主动 Request 和 ServeAsync 发出的 Reply 都通过同一个 `WriteAsync`。`SemaphoreSlim` 只把写入串行化，不会一直持有到响应返回，因此一条连接上可以存在多个 waiting 项，按各自请求 ID 完成。

`Close` 不直接关闭底层流，也不发送远端取消消息。调用超时仅终止本地等待，远端可能仍会发送回复。请求信封只要求 ID 非空，并没有在这里要求雪花 ID 格式；正常节点调用会用 `IdGenerator` 生成规范 ID。
