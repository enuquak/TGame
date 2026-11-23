# DynamicTopologyChecks.cs 代码详解

源码：[code/tests/engine/DynamicTopologyChecks.cs](../../../../../code/tests/engine/DynamicTopologyChecks.cs)

## 职责与测试环境

本检查通过 `TestServices`、实际连接配置和 `FrameStream` 验证节点首次达标后在连接增减时保持 `Ready`。`RunAsync(stop)` 不启动网络监听，也不做真实服务发现，而是直接调用 `ServerNode.RunLinkAsync` 注入已指定对端的内存连接。

它紧接已读的 [StartupReadinessChecks.cs](StartupReadinessChecks.cs.md)：前者关注首次门槛和零门槛，本文件用一组连续操作展示“首连完成之后”的状态。测试方法里的 `Check` 是普通条件断言，条件为 false 就抛异常，没有隐藏的自动重试。

## 先认清对象之间的关系

| 对象 | 用途 |
| --- | --- |
| `router` | 无主动依赖的 Router1，只检查构造后能否直接就绪 |
| `node` | 主角 Base42，主动依赖 Router 和 DBMgr |
| `streams` | 按对端实例名保存内存输入/输出通道 |
| `tasks` | 按对端实例名保存整条连接的生命周期任务 |
| `lifetime` | 关联外部 stop，测试结束时统一取消尚存的连接 |

Base42 和 Gate77 中的编号故意远大于默认启动数量。它们用于说明“本轮启动多少个”与“哪些编号可以合法存在”是不同概念。实例名仍通过 `ServerProcess.FormatName` 生成，不在测试里拼接命名规则。

`streams` 和 `tasks` 存的是两个不同事实：前者让测试能模拟对端发消息或结束输入，后者让测试知道引擎什么时候真正完成退出。仅结束通道而不等待任务，可能在清理尚未完成时就做下一步断言。

## 场景顺序

1. 创建没有主动依赖的 Router，立即要求 `FirstLinks` 成功完成且 `Ready` 为真，说明它不等待入站连接。
2. 创建 `Base42`。先接入 `Router1`，要求仍未完成首连，因为还缺 DBMgr 类型；再接入 `DBMgr1`，等待 `FirstLinks` 并要求就绪。
3. 检查 `Gate77` 可以作为入站身份，证明实例编号不受初始启动数量限制；无对应入站关系的 `Scene1` 必须被拒绝。
4. 添加 `Router2`，依次断开 `Router1`、`Router2`。多余连接和失去冗余连接不影响就绪，失去最后一条 Router 连接也保持 `Ready` 为真，已经完成的 `FirstLinks` 保持完成。
5. `WaitUntilReadyAsync` 在尚未重连时即可完成，再接入 `Router9`，验证替代实例可以重新加入。
6. 添加、断开 Gate 入站链路，要求两次操作都不改变 Base 的就绪状态。

可以对照下面的状态表读每一个 `Check`：

| 阶段 | Base42 的相关连接 | 首次要求是否曾满足 | `Ready` |
| --- | --- | --- | --- |
| 只接入 Router1 | Router1 | 否，缺 DBMgr | false |
| 再接入 DBMgr1 | Router1、DBMgr1 | 是 | true |
| 增加 Router2 | Router1、Router2、DBMgr1 | 已满足 | true |
| 关闭 Router1 | Router2、DBMgr1 | 已满足 | true |
| 再关闭 Router2 | DBMgr1 | 已满足 | true |
| 接入 Router9 | Router9、DBMgr1 | 已满足 | true |

`await node.WaitUntilReadyAsync(stop)` 放在没有 Router 连接的阶段，是一个有意的检查：如果实现仍按“当前每种依赖至少有一条”等待，它就不能立即结束。当前语义只等待首次完成任务，历史达标后不会因普通断线而撤销。

Gate 的入站测试还有两层含义：`AcceptsIncoming` 先检查静态类型方向；随后通过 `Open(gate)` 真正进入公共连接逻辑，并从输出通道读取回复的 Hello，才说明该内存连接已处理到相应阶段。它没有执行 ZooKeeper 占号校验。

## 辅助流程与清理

局部函数 `Open(peer)` 创建流、预写 Hello、分配链路 ID，并保存链路任务。`Close(peer)` 完成输入通道，等待链路退出，保证断线后的断言发生在清理完成之后。整个场景使用关联取消源；`finally` 取消剩余任务并逐一等待，仅忽略由该取消源导致的 `OperationCanceledException`。

`Open` 的关键代码是：

```csharp
stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
streams[peer] = stream;
tasks[peer] = node.RunLinkAsync(peer, services.Ids.GenId(), stream, stream, lifetime.Token,
    requireReady: node.AcceptsIncoming(peer));
```

先预写 Hello，再调用 `RunLinkAsync`，让节点读到预期身份。两个 `stream` 参数是同一个 [FrameStream](FrameStream.cs.md) 对象：它同时实现流读取接口和写入接口，但内部 `Input` 与 `Output` 是不同通道，不会把自己发出的帧当成收到的帧。

这里保存 Task 而没有立即 await，是因为该 Task 代表连接直到断开的整个生命周期。如果在 `Open` 中直接等它结束，后续“增加第二个连接”和“主动关闭第一个连接”就无法按计划执行。提前准备好的 Hello 允许当前内存实现同步推进握手；这种测试安排不能照搬为真实网络时序保证。

`requireReady` 根据 `AcceptsIncoming(peer)` 决定：Gate 是 Base 的入站邻居，模拟其连接时走要求本端已就绪并回复 Hello 的分支；Router/DBMgr 则模拟本端的主动目标。这里直接调用公共连接方法，不经过 `ConnectAsync` 的真实拨号或 `AcceptConnectAsync` 的入口身份验证。

`Close` 使用两个动作完成断线：

```csharp
streams[peer].Input.Writer.Complete();
await tasks[peer].WaitAsync(stop);
```

`Complete` 让内存输入读完已有数据后得到流结束；等待连接任务则保证 `RunLinkAsync` 的 `finally` 已经注销连接并关闭对应 `PeerLink`。因此后面的 Ready 断言不是与断线清理竞速。

最后无论哪个断言失败，`finally` 都取消 `lifetime` 并等待字典中的任务。`catch ... when` 只把本测试取消导致的 `OperationCanceledException` 视作预期收尾，其他故障仍会暴露。

## 限制

该测试使用 `TestServices.Startup` 的各类型门槛 `1`，只证明按类型的首次就绪和运行期连接增减行为；首连多实例门槛另由 `StartupReadinessChecks` 覆盖。它没有检验 ZooKeeper 临时占号、地址发布或真实传输重连。

此处关闭了所有 Router 连接，但 DBMgr 仍在；不要把这一段单独解释成“所有依赖都断开”的证明。空发现列表、真实 gRPC 连接替换和恢复由 [DiscoveryLinkChecks.cs](DiscoveryLinkChecks.cs.md) 覆盖。也不能从 Ready 为 true 推导 Router RPC 此时可用：Ready 记录首次启动完成，调用可用性仍取决于对应连接。
