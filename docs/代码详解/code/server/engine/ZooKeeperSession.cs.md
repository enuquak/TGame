# ZooKeeperSession.cs 代码详解

源码：[code/server/engine/ZooKeeperSession.cs](../../../../../code/server/engine/ZooKeeperSession.cs)

## 客户端事件如何变成可等待的状态

`ZooKeeperProxy` 构造时持有一个 `ZooKeeperSession`，建立客户端时把它交给 ZooKeeper 库作为 `Watcher`。库调用 `public override Task process(WatchedEvent evt)`，其中 `override` 表示实现基类的虚方法。这里同步把 `evt.getState()` 传给 `StateChanged`，随后返回已经完成的 `Task.CompletedTask`。状态回调只转换信号，网络关闭由代理负责。见 [ZooKeeperProxy.cs](ZooKeeperProxy.cs.md)。

| 成员 | 此处表示的事实 |
| --- | --- |
| `connected` | 是否已收到成功连接事件，且尚未被失效逻辑清除 |
| `failed` | 会话已永久失效，只会从假变真 |
| `Connected` | 暂停初始化等待的条件已经解除，随后仍需检查会话 |
| `Lost` | 旧会话已不可用于本进程的身份和 worker |

两个布尔字段使用 `volatile`，用于其他线程观察状态写入。两个信号是无结果的 `TaskCompletionSource`，代理从它们读取 `.Task` 来等待，状态类通过 `TrySetResult()` 完成信号。任务只完成一次，重复通知可以安全尝试完成。`RunContinuationsAsynchronously` 让恢复的等待方不在当前状态切换回调里同步执行。

## 依次推演 StateChanged

1. 收到 `SyncConnected` 且 `!failed`，先设 `connected = true`，再完成 `Connected` 并返回。代理初始化醒来后检查取消和 `CheckActive()`，确认活动后才公布初始化成功。
2. 收到 `Expired` 或 `AuthFailed`，直接 `Invalidate()`。`state is ... or ...` 把两个枚举值作为可选模式。另一条失效路径是 `state == Disconnected && connected`。C# 的 `&&` 优先级高于 `||`，因此只给断开分支加上曾经连通的条件。
3. `Invalidate()` 先设置 `failed = true`，再清除 `connected`，然后完成 `Connected` 和 `Lost`。初始化阶段遇到鉴权失败也会被唤醒，此时 `CheckActive()` 会抛 `InvalidOperationException`。代理主动关闭同样走这个方法。

例如首次连接阶段收到 `Disconnected`，当时 `connected` 为假，两个任务都可继续等待。接着收到 `SyncConnected` 后，会话通过活动检查。若此后再收到 `Disconnected`，`Lost.Task` 立即完成。即使库接着报告 `SyncConnected`，`failed` 仍为真，旧会话保持失效。

## 上层如何响应

代理在占号、发布和发现等操作调用 `CheckActive`。内部 ID 生成器在每次发号前也检查同一会话，并公开同一个 `Lost.Task` 作为 `LeaseLost`。App 通过代理的 `SessionLost` 监督后台工作，会话失效时触发宿主停止，重新启动后再获得身份和 worker。这与进程的 `Ready` 里程碑分别由各自状态决定。见 [SnowflakeIdGenerator.cs](identifiers/SnowflakeIdGenerator.cs.md)、[Program.cs](../app/Program.cs.md)、[ZooKeeperProxyChecks.cs](../../tests/engine/ZooKeeperProxyChecks.cs.md)。
