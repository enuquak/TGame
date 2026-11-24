# ZooKeeperSession.cs 代码详解

源码：[code/server/engine/ZooKeeperSession.cs](../../../../../code/server/engine/ZooKeeperSession.cs)

这个类把 ZooKeeper 客户端发来的连接事件，转换成本进程可以检查的状态和可以等待的通知。它不负责建立网络连接，也不负责分配 worker 编号；这些操作在 [ZooKeeperProxy.cs](ZooKeeperProxy.cs.md) 中。先读懂它，就能解释为什么连接失败会结束初始化、为什么断连后停止发号，以及为什么旧实例不会因重连通知自动恢复。

## 1. Watcher 是事件入口，Session 保存本地状态

```csharp
internal sealed class ZooKeeperSession : Watcher
```

`internal` 表示引擎程序集内部使用；`sealed` 表示不能再被其他类继承；`: Watcher` 表示继承 ZooKeeper 客户端库提供的观察者基类。这里的类名带 Session，是本项目对会话状态的包装，并不是 ZooKeeper 客户端本身。

代理创建实际客户端时传入这个观察者：

```csharp
zk = new ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, session);
```

最后的 `session` 就是 `ZooKeeperSession` 对象。客户端得到会话事件后，会通过观察者的 `process` 方法交给它。业务代码通常不直接调用这个回调，测试为了精确构造场景会直接调用 `StateChanged`。

可以把调用关系理解为：

```text
ZooKeeper 客户端收到事件
    → session.process(evt)
    → StateChanged(evt.getState())
    → 更新状态，必要时完成通知任务
    → 初始化等待方、发号器和宿主观察到结果
```

## 2. connected 和 failed 为什么同时存在

```csharp
private volatile bool connected;
private volatile bool failed;
```

两个 `bool` 字段默认都是 `false`。它们表达的事实不同：

| 字段 | 表达的事实 |
| --- | --- |
| `connected` | 本包装当前是否认可会话处于已连接状态 |
| `failed` | 本包装是否已经认定这个会话永久失效 |

刚创建时是 `connected = false, failed = false`：还没有连接成功，但仍允许将来连接成功。

正常连接后是 `true, false`。失效后是 `false, true`：不仅当前不连接，这个对象还记住了失效历史，以后不会自行恢复。

因此不能简单地把 `failed` 写成 `!connected`。否则“还在第一次连接”和“过去连接过但已失效”就变成同一种状态，后面的处理逻辑会丢失区别。

`volatile` 用于跨线程状态读写的可见性与顺序约束。客户端回调更新这些字段，其他线程可能同时读取。它不等同于 `lock`，也不会让两个字段的组合更新变成一个不可分割的操作；不要从这两个修饰符推导出所有并发时序都被锁住。

## 3. Connected 和 Lost 是怎样发通知的

```csharp
internal TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
internal TaskCompletionSource Lost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
```

`TaskCompletionSource` 可以理解为一个任务的“完成控制端”。它提供 `.Task` 给等待方，持有完成源的一方则通过 `TrySetResult()` 通知等待方继续。

这里使用不带泛型参数的版本，因此任务完成时没有额外业务结果。`Connected` 和 `Lost` 的类型都是完成源，它们的 `.Task` 才是调用方真正等待的任务。

`{ get; }` 是只有 getter 的自动属性，初始化后不能在普通方法里重新给属性赋值。但“不能替换完成源对象”不代表不能调用该对象的方法；`Connected.TrySetResult()` 完全合法。

右边 `new(...)` 是目标类型推断，编译器从左边知道要创建 `TaskCompletionSource`。展开后相当于写 `new TaskCompletionSource(...)`，只是省略重复的类型名称。

示意用法如下，表示接口的分工，不是额外业务实现：

```csharp
// 等待方取得任务。
Task notification = session.Lost.Task;

// 发生事件的一方完成它。
session.Lost.TrySetResult();

// 此时任务已经完成，这个 await 可以直接继续。
await notification;
```

任务与瞬时通知不同：事件先发生，后来的等待方仍能看到完成状态。通知不会因为“先发通知、后开始 await”就消失。

`RunContinuationsAsynchronously` 要求完成源调度后续延续时，不把这些延续直接内联到完成任务的调用栈里。这样，在 ZooKeeper 回调里完成一个任务时，不必同步执行等待方可能很复杂的后续代码。它不代表构造任务时新开一个专用线程，也不承诺每个等待者最终运行在哪个线程上。

## 4. process：只取出状态并转交

```csharp
public override Task process(WatchedEvent evt)
{
    StateChanged(evt.getState());
    return Task.CompletedTask;
}
```

`override` 表示重写基类的事件回调，方法名的小写 `process` 遵循库的接口，不能因为本项目习惯 PascalCase 就随意改名。

`evt.getState()` 取出事件中的会话状态，交给 `StateChanged`。整个方法没有需要等待的 IO，所以直接返回 `Task.CompletedTask`，表示回调已经处理完成。

接口要求返回 `Task`，不意味着实现必须加 `async`，也不意味着一定发生异步等待。这里同步更新状态，然后返回一个已经完成的任务即可。

## 5. StateChanged：怎样区分连接、初次断开和失效

连接成功分支是：

```csharp
if (state == Event.KeeperState.SyncConnected && !failed)
{
    connected = true;
    Connected.TrySetResult();
    return;
}
```

`&&` 表示两个条件都成立：收到连接成功事件，并且这个包装此前没有被判定失效。`!failed` 是逻辑取反。

进入分支后，先把 `connected` 设为真，再完成 `Connected`，让等待初始化的代码有机会继续检查状态。最后的 `return` 结束本次处理。

第二个条件决定什么时候失效：

```csharp
if (state is Event.KeeperState.Expired or Event.KeeperState.AuthFailed ||
    state == Event.KeeperState.Disconnected && connected)
{
    Invalidate();
}
```

`state is A or B` 是模式匹配，表示状态是 A 或 B。这里判断“会话过期”或“认证失败”。后半部分的 `&&` 比 `||` 结合得更紧，所以整个条件可以读成：

```text
会话过期
或者认证失败
或者（收到断开事件，并且此前已经认可为已连接）
```

| 收到的事件 | 条件 | 行为 |
| --- | --- | --- |
| `SyncConnected` | `failed` 为假 | 标记连接并唤醒连接等待方 |
| `Disconnected` | 还没有连接成功 | 保持未连接，继续允许首次连接 |
| `Disconnected` | 已连接 | 调用 `Invalidate`，永久失效 |
| `Expired` / `AuthFailed` | 无论是否曾经连接 | 调用 `Invalidate` |
| `SyncConnected` | 已经 `failed` | 不恢复旧会话状态 |

这个方法没有为所有枚举状态都编写分支。未匹配的状态在当前代码中不会修改这些字段，不能据此把整个客户端的所有状态都解释成已被完整处理。

## 6. Invalidate：为什么同时完成两个任务

```csharp
internal void Invalidate()
{
    failed = true;
    connected = false;
    Connected.TrySetResult();
    Lost.TrySetResult();
}
```

前两句记录不可恢复的失效状态。`Lost.TrySetResult()` 用来通知正在监控失效的宿主和 ID 服务，这一点比较直观。

容易误读的是 `Connected.TrySetResult()`：既然连接失败，为什么还完成叫 Connected 的任务？因为初始化可能仍在等待第一次连接。如果认证失败发生在连接成功前，单独完成 `Lost` 不能让那个等待方醒来。

这里把 `Connected` 当作“初始化等待可以结束”的信号。任务完成后，调用方必须再检查真实状态，而不能只根据任务成功完成就宣称连接成功。

对应 `ZooKeeperProxy.InitializeAsync` 的实际顺序：

```csharp
await session.Connected.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
timeout.Token.ThrowIfCancellationRequested();
session.CheckActive();
initialized = true;
```

如果是连接成功，`CheckActive()` 通过，才写入 `initialized = true`。如果是认证失败唤醒了任务，`CheckActive()` 会抛异常，初始化进入清理路径。

`TrySetResult()` 中的 Try 表示尝试完成：任务尚未完成时返回 `true`，已经完成时返回 `false`。这里不使用这个返回值。重复失效或先连接后失效时，再完成同一个任务不会因为“已经完成”而抛异常。

这也说明 `Task.IsCompletedSuccessfully` 只描述任务自身的完成方式，不代表它通知的业务事件是好事。`Lost` 正常完成，恰好表示失效已经发生。

## 7. CheckActive：把不可用状态变成明确异常

```csharp
internal void CheckActive()
{
    if (!connected || failed)
    {
        throw new InvalidOperationException("The ZooKeeper session is unavailable; restart this process to acquire a new identity.");
    }
}
```

`||` 表示只要有一个条件成立就拒绝使用：没有连接，或者已经失效。两个条件都不成立时，方法正常返回，没有业务返回值。

调用它不发起网络请求，也不等待重连。它只是读取当前记录在内存中的状态。网络变化要先被客户端观察并交给回调，之后本地检查才能反映该变化；不能把它理解成每次查询 ZooKeeper 服务器的实时证明。

代理在申请、发现、发布等步骤前后调用它；`SnowflakeIdGenerator.GenId()` 在计算 ID 前调用它。这样，即使不同调用方不主动等待 `Lost`，进行关键操作时也会拒绝使用失效会话。

## 8. 把三个典型过程串起来

**第一次连接还没成功：** 两个字段均为假。初次 `Disconnected` 不触发失效，`Lost` 不完成。之后收到 `SyncConnected`，可进入可用状态。

**连接成功后断开：** `StateChanged(Disconnected)` 调用 `Invalidate()`，`failed` 永久变真，`Lost` 完成。后来的 `SyncConnected` 因 `!failed` 不成立而不能恢复发号。

**连接成功前认证失败：** `Invalidate()` 同时完成 `Connected` 和 `Lost`。初始化结束等待，在 `CheckActive()` 处失败，不会悬挂到整个连接期限耗尽才发现错误。

这些过程分别由 [ZooKeeperProxyChecks.cs](../../tests/engine/ZooKeeperProxyChecks.cs.md) 的本地会话检查验证；[IdGeneratorLifecycleChecks.cs](../../tests/engine/IdGeneratorLifecycleChecks.cs.md) 检查了失效通知和发号行为之间的连接。

## 9. 谁观察 Lost，谁负责释放

`ZooKeeperProxy.SessionLost` 暴露 `session.Lost.Task`。`WorkerLease` 持有同一个会话，实际 ID 生成器的 `LeaseLost` 也会引用这个任务。因此进程身份与 ID worker 的可用性在当前实现中受同一会话控制。

[Program.cs](../app/Program.cs.md) 监控 `SessionLost`，通知完成后抛异常，由监督逻辑取消进程生命周期并执行清理。`ZooKeeperProxy.CloseAsync` 也会主动调用 `Invalidate()`，所以正常释放同样通知等待方当前会话已不可用。

这个类没有 `DisposeAsync`，不负责关闭网络客户端；关闭由代理执行。它只负责状态与通知，避免把“设置失效标记”和“真正释放连接”混成一个操作。

当前“曾连接后断线就永久失效”是项目策略。ZooKeeper 本身区分短暂断连和会话过期，客户端有可能在原会话有效时重连；服务器在会话过期时清理所属临时节点。参见 [ZooKeeper 官方会话说明](https://zookeeper.apache.org/doc/current/zookeeperProgrammers.html#ch_zkSessions)。阅读这里时应以实际 `failed` 分支为准，不能把协议可重连自动理解为这个包装也会恢复。
