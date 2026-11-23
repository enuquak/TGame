# IdGenerator.cs 代码详解

源码：[code/server/engine/identifiers/IdGenerator.cs](../../../../../../code/server/engine/identifiers/IdGenerator.cs)

## 谁创建，谁调用

`IdGenerator` 是宿主容器注册的进程级服务。App 连接 ZooKeeper 并取得进程身份后，显式等待 `ids.InitializeAsync()`，随后才解析 `ServerNode` 和启动监听。节点主动拨号时生成连接 ID，调用已连接目标时生成 RPC 请求 ID。具体时钟、序列和 worker 租约都在内部生成器里。见 [ServerServices.cs](../../app/ServerServices.cs.md)、[Program.cs](../../app/Program.cs.md)、[ServerNode.cs](../ServerNode.cs.md)、[SnowflakeIdGenerator.cs](SnowflakeIdGenerator.cs.md)。

## 构造入口和字段

公开构造函数接收已经加载的配置和共享代理。`: this(stop => SnowflakeIdGenerator.CreateAsync(options, zooKeeper, stop))` 先把 lambda 交给本类的内部构造函数，再执行公开构造函数里的空值检查。此时保存的是一段以后执行的代码，真正访问 ZooKeeper 要等调用工厂。

- `create` 的类型是 `Func<CancellationToken, Task<SnowflakeIdGenerator>>`。第一个类型参数是输入，最后一个是返回值。测试也能用这个入口注入成功、失败或取消的异步创建过程。
- `generator` 初始为 `null`，只有创建成功且通过最后一次取消检查才交给该字段。
- `lifecycle = new(1)` 是初始许可为 `1` 的 `SemaphoreSlim`。初始化和释放都需取得这一个许可，可以在持有许可期间 `await`，之后在 `finally` 归还。
- `disposed` 表示服务已经进入释放状态，即使初始化还没有成功，释放也会设置它。

## InitializeAsync 如何接管资源

1. `await lifecycle.WaitAsync(stop)` 等待操作许可。如果在等待许可时取消，方法会在进入 `try` 前结束，自然无需归还许可。
2. 取得许可后检查是否已释放，再检查 `generator is not null`。已初始化则抛 `InvalidOperationException`。这两个检查与接下来的创建流程都在许可内，所以两个并发的初始化调用不会同时创建 worker。
3. `await create(stop)` 等工厂返回。工厂抛异常时直接向外传播，`finally` 仍然执行，也没有生成器被写入字段。
4. 工厂可能在调用方发出取消后仍返回已经创建的生成器。这里再次检查 `stop.IsCancellationRequested`。若已请求取消，先等待这份局部对象释放，再通过 `ThrowIfCancellationRequested()` 抛出取消。
5. 成功路径用 `Volatile.Write(ref generator, initialized)` 发布实例。`ref` 允许 API 直接访问字段的位置。日常调用用相应的 `Volatile.Read` 读取引用，无需进入生命周期信号量。这里的读写负责发布引用，实际生成序号的互斥由内部生成器负责。

例如工厂完成 worker 认领后，启动取消恰好已经发出。初始化此时仍持有许可，先归还尚未交给字段的租约，再报取消。随后即使调用 `GenId()`，也只能看到尚未初始化的服务。

这里对被等待的任务调用 `ConfigureAwait(false)`，允许后续代码在无须返回调用方上下文的情况下继续。`await` 仍然会等待操作完成并传播异常，信号量也仍由 `finally` 归还。

## 读取与释放

`Current` 是私有计算属性。它先通过 `ObjectDisposedException.ThrowIf(disposed, this)` 检查服务，再读取 `generator`。`?? throw ...` 表示只在读到空引用时抛出未初始化错误。`GenId() => Current.GenId()` 是只有一个表达式的方法，`LeaseLost => Current.LeaseLost` 则是只读属性。两者都经过相同的状态检查。生产宿主监督 `ZooKeeperProxy.SessionLost`，`LeaseLost` 转发内部生成器暴露的同一会话失效任务。

`DisposeAsync` 等待生命周期许可后检查是否已释放，第一次进入会先设 `disposed`，再按需等待内部生成器归还 worker。之后再次进入可以直接结束。此方法返回可等待的 `ValueTask`，也是 `IAsyncDisposable` 接口要求的返回类型。调用方可以用 `await using` 等待异步释放，生产环境的共享实例由宿主容器在清理时负责，释放顺序是先 ID 服务，再 ZooKeeper 代理。见 [ZooKeeperProxy.cs](../ZooKeeperProxy.cs.md)、[IdGeneratorLifecycleChecks.cs](../../../tests/engine/IdGeneratorLifecycleChecks.cs.md)。
