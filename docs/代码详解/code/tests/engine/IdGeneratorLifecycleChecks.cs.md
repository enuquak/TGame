# IdGeneratorLifecycleChecks.cs 代码详解

源码：[code/tests/engine/IdGeneratorLifecycleChecks.cs](../../../../../code/tests/engine/IdGeneratorLifecycleChecks.cs)

## 用本地工厂控制初始化结果

`RunAsync` 从默认或 `--engine` 模式以及 `--zookeeper-session` 模式调用。测试为传入令牌的等待设置 `5` 秒期限。每组用例构造自己的 `IdGenerator`，通过内部构造函数注入工厂，直接返回本地 `SnowflakeIdGenerator` 或指定异常。对会话的操作由测试发送状态事件。见 [测试入口](Program.cs.md)、[IdGenerator.cs](../../server/engine/identifiers/IdGenerator.cs.md)。

| 准备和操作 | 断言验证的状态变化 |
| --- | --- |
| `failed` 的工厂用 `Task.FromException<SnowflakeIdGenerator>` 返回一个失败任务 | 初始化前访问 `GenId` 或 `LeaseLost` 抛 `InvalidOperationException`。等待初始化传播原 `IOException`，接着发号仍失败 |
| 工厂已经持有 `abandoned`，在返回它之前调用 `cancelled.Cancel()` | 外层初始化抛 `OperationCanceledException` 之前先释放这份生成器。直接调 `abandoned.GenId()` 抛 `ObjectDisposedException`，通过 `interrupted.GenId()` 则仍是未初始化 |
| 用前一组已取消的令牌进行初始化，工厂如被调用会写 `invoked = true` | 必须抛取消，且 `invoked` 保持为假，说明开始时的取消阻止工厂执行 |
| 工厂返回正常生成器，第一次初始化完成，再次调用初始化 | 第二次应抛 `InvalidOperationException`。随后并发调用同一个服务发 `5000` 个 ID，以 `ConcurrentDictionary.TryAdd` 检查重复 |

`Task.FromException<T>` 创建已经失败的任务，错误在 `await` 这个任务时传播。`Task.FromResult(...)` 则创建已经成功的任务。工厂可以用这两个 API 隔离需要观察的时机，无需额外线程。这也是为何能精确覆盖「创建已有结果，发布前收到取消」的分支。

## 释放、实例隔离和失效转发

并发场景包在 `await using (ids) { ... }` 内。作用域结束时语言会等待 `DisposeAsync`，随后分别检查 `generator.GenId()` 和 `ids.GenId()` 都抛已释放异常，再显式释放检查重复调用。

下一组创建新的会话，先注入 `SyncConnected`，然后用 `session: session` 这个命名参数构造新的生成器。经 `nextIds` 初始化和发号后，用 `ReferenceEquals(nextIds.LeaseLost, next.LeaseLost)` 比较任务引用，以确认服务原样转发。再释放一次旧 `ids` 后，新实例仍能生成合法 ID。旧实例和新实例用相同的 worker 编号，但各自管理生命周期，检查关注的是单个服务释放的作用域。

对新实例的会话注入 `Disconnected`，先等待 `nextIds.LeaseLost`，再要求发号抛 `InvalidOperationException`。即使随后注入 `SyncConnected`，旧会话仍失效。显式释放新服务后，两层发号入口都变为抛 `ObjectDisposedException`。见 [ZooKeeperSession.cs](../../server/engine/ZooKeeperSession.cs.md)、[SnowflakeIdGenerator.cs](../../server/engine/identifiers/SnowflakeIdGenerator.cs.md)。

`Expect<T>` 调用同步委托，`ExpectAsync<T>` 会等待返回的任务再捕获异常。两者只接收匹配的异常类型，避免把意外失败算作检查通过。这里验证工厂接管、状态隔离和失效转发，ZooKeeper 临时节点的实际分配、竞争和删除由集成入口覆盖。见 [IdGeneratorChecks.cs](IdGeneratorChecks.cs.md)。
