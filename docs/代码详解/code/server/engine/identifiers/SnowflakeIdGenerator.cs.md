# SnowflakeIdGenerator.cs 代码详解

源码：[code/server/engine/identifiers/SnowflakeIdGenerator.cs](../../../../../../code/server/engine/identifiers/SnowflakeIdGenerator.cs)

## 从进程服务走到实际发号

宿主完成 ZooKeeper 连接和进程占号后，调用 `IdGenerator.InitializeAsync()`。公开的 `IdGenerator` 持有本文件里的内部生成器，调用 `ids.GenId()` 时最终走到这里。发号时使用当前 UTC 时间、从 ZooKeeper 租到的 worker 编号和该毫秒内的序号，具体位拼装交给 `SnowflakeSequence`。见 [启动入口](../../app/Program.cs.md)、[IdGenerator.cs](IdGenerator.cs.md)、[SnowflakeSequence.cs](SnowflakeSequence.cs.md)。

## 构造函数与 CreateAsync

`CreateAsync(options, zooKeeper, stop)` 先拒绝空配置，然后将 `options.WorkerRootPath` 和取消令牌传给共享代理的 `AcquireWorkerAsync`。等待成功后才用取得的 `WorkerLease` 构造实例。因此分配失败、编号用尽或申请等待被取消时，异常由申请路径传给上层，调用方拿不到生成器。见 [ZooKeeperProxy.cs](../ZooKeeperProxy.cs.md)。

私有构造函数使用 `: this(worker.WorkerId, TimeProvider.System, worker.Session)`。C# 的 `: this(...)` 表示先调用本类的另一个构造函数，再执行自己的函数体。共用的内部构造函数保存时钟和会话，建立该 worker 独享的 `sequence`，从会话取出失效任务。私有构造函数的函数体再保存租约，以便释放时归还 worker。内部构造函数也支持测试直接传入编号、自定义时钟和可选会话。

```csharp
LeaseLost = session?.Lost.Task ?? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
```

`?.` 在 `session` 为 `null` 时让左侧结果为 `null`。`??` 只在左侧为 `null` 时计算右侧。生产实例直接复用 `session.Lost.Task`，从初始化到释放期间读取到的是同一个任务。无会话的测试实例得到一个本类从未主动完成的任务。

## GenId 如何推进一次发号

1. 进入 `while (true)` 循环，使用 `lock (sync)` 串行化同一生成器上的调用。锁内先检查 `disposed`，再执行 `session?.CheckActive()`。测试可以省略会话，生产路径只有活动会话下才继续读时钟。
2. `clock.GetUtcNow().ToUnixTimeMilliseconds()` 得到自 Unix 纪元起的 UTC 毫秒数。减去 `SnowflakeSequence.EpochMilliseconds`，得到相对项目纪元 `2026-01-01 00:00:00 UTC` 的毫秒数。
3. `sequence.TryGenId(timestamp)` 返回 `long?`。这是可空值类型，既可装一个 `long`，也能表示本毫秒已经发完。`id.HasValue` 为真时，再安全地取 `id.Value` 并用 `InvariantCulture` 格式化为十进制字符串。即使在锁体内 `return`，C# 也会先释放锁。
4. 返回 `null` 时，先走出锁，再调用 `Thread.Sleep(SequenceWaitMilliseconds)`，最后从循环头重试。睡眠的线程同步阻塞，下一轮仍会检查释放状态和会话状态。另一个线程在这段睡眠期间可以取得锁，尝试发号或进行释放。

例如 worker `42` 在相对毫秒 `100` 第一次发出 `"419602432"`。同一毫秒下一次会发出 `"419602433"`。如果序号已用到 `4095`，下一次会在锁外睡眠并重新读时钟，直到毫秒前进或检查失败。`GenId()` 没有接收取消令牌，时钟持续停在已经用尽的毫秒时，线程会继续循环。时钟回拨或越过可表示范围则由 `TryGenId` 直接抛出 `InvalidOperationException`，立即结束这次调用。

## 会话失效与释放

会话曾连接后断开时，`LeaseLost` 完成，之后进入 `GenId` 的调用会在会话活动检查处失败。会话状态由代理和 watcher 更新，`sync` 只保护本生成器的序列和释放状态。生产宿主直接监督该代理的 `SessionLost`，和生成器暴露的是同一个会话失效任务。见 [ZooKeeperSession.cs](../ZooKeeperSession.cs.md)。

`DisposeAsync` 在同一把锁内检查并设置 `disposed`，随后退出锁，再异步等待租约释放。这样，即使删除 worker 节点所需的网络操作尚未完成，后面的发号调用也已会收到 `ObjectDisposedException`。测试实例没有 `worker` 引用时，释放在设置标记后结束。释放单个 worker 成功时，共享会话仍供进程身份和发现使用。归还租约的失败处理由代理负责。

跟读验证可从固定时钟的回拨案例看序号何时变化，再看真实 ZooKeeper 分配与回收案例。见 [IdGeneratorChecks.cs](../../../tests/engine/IdGeneratorChecks.cs.md)。
