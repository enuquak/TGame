# IdGeneratorLifecycleChecks.cs 代码详解

源码：[code/tests/engine/IdGeneratorLifecycleChecks.cs](../../../../../code/tests/engine/IdGeneratorLifecycleChecks.cs)

这份文件检查 `IdGenerator` 从创建、初始化、使用，到失效和释放的整个生命周期。阅读时要同时看懂两件事：被测服务应当怎样工作，以及测试怎样构造场景来证明这个行为。

| 场景 | 预期行为 |
| --- | --- |
| 尚未初始化 | 不能生成 ID，也不能读取租约监控任务 |
| 初始化失败 | 异常向外传播，服务保持未初始化状态 |
| 创建函数返回对象时已收到取消 | 释放新对象，初始化以取消结束 |
| 开始初始化前已经取消 | 创建函数不应执行 |
| 正常初始化后再次初始化 | 第二次调用抛出异常 |
| 并发使用同一个实例 | 本次生成的 5000 个 ID 不重复 |
| 释放外层服务 | 内部生成器同时被释放，重复释放安全 |
| 创建另一个实例 | 新旧实例的生命周期相互独立 |
| 已连接的会话发生断线 | 发出失效通知，停止生成 ID |
| 失效后再次收到连接成功事件 | 旧生成器仍不能恢复使用 |

下面逐段解释这些场景。表格用于定位，具体的语法和执行过程在对应段落展开。

## 1. 先分清被测对象与测试入口

测试里经常同时出现 `IdGenerator` 和 `SnowflakeIdGenerator`：

```text
调用方
  ↓
IdGenerator：检查初始化状态，管理内部对象的创建与释放，提供对外入口
  ↓
SnowflakeIdGenerator：实际发号，检查自身和会话状态，保护雪花序列
```

例如变量 `ids` 是外层服务，`generator` 是它持有的实际生成器。释放后分别调用两个对象，是在确认外层和内层都结束了生命周期。对照 [IdGenerator.cs](../../server/engine/identifiers/IdGenerator.cs.md) 和 [SnowflakeIdGenerator.cs](../../server/engine/identifiers/SnowflakeIdGenerator.cs.md)。

文件开头引用的 `System.Collections.Concurrent` 提供并发字典；`org.apache.zookeeper` 提供会话状态枚举；`TGame` 提供 `IdText`；`TGame.Server.Engine` 提供引擎中的 ID 服务和会话类型。这里引用 ZooKeeper 类型不代表测试会建立真实网络连接。

```csharp
internal static class IdGeneratorLifecycleChecks
{
    private const int WorkerId = 42;
    private const int ConcurrentIds = 5000;
    private const int TestTimeoutSeconds = 5;
```

`internal` 表示这个类供当前程序集内部使用。`static` 表示不需要创建测试类实例，可以直接调用 `IdGeneratorLifecycleChecks.RunAsync()`。`private const int` 表示仅在类内部使用的整数常量，三个值分别是测试 worker 编号、生成数量和取消期限。

`WorkerId = 42` 是本地用例选择的固定编号，不是从 ZooKeeper 分配来的编号。`TimeProvider.System` 则会让生成器使用实际系统时间。

入口声明为：

```csharp
public static async Task RunAsync()
```

`Task` 表示调用方可以等待这组检查结束；没有 `Task<T>` 中的结果类型，说明完成后不返回业务结果。`async` 允许方法内使用 `await`，本身不代表新建线程。

[测试入口](Program.cs.md) 在默认、`--engine` 和 `--zookeeper-session` 路径中调用它；[IocChecks.cs](IocChecks.cs.md) 也会调用它。理解本文件不需要启动这些入口，某个入口中其他检查的外部依赖也不等于本文件需要那些依赖。

## 2. 为什么构造函数里可以传一个函数

测试使用了 `IdGenerator` 的内部构造函数：

```csharp
internal IdGenerator(Func<CancellationToken, Task<SnowflakeIdGenerator>> create)
{
    this.create = create;
}
```

`Func<CancellationToken, Task<SnowflakeIdGenerator>>` 描述一个函数的形状：接收一个 `CancellationToken`，返回一个 `Task<SnowflakeIdGenerator>`。等待这个任务成功完成后，可以得到实际生成器。

构造函数只把传入的函数保存在字段中。它不会在赋值时自动执行。真正调用的位置在 `InitializeAsync` 内：

```csharp
var initialized = await create(stop).ConfigureAwait(false);
```

这一句先调用保存的创建函数，再等待结果，把得到的生成器交给局部变量 `initialized`。`ConfigureAwait(false)` 表示等待后不要求回到原来的同步上下文；它不会让创建函数自动变成后台线程执行。

正常运行时，公开构造函数把 `SnowflakeIdGenerator.CreateAsync(options, zooKeeper, stop)` 包装成这个函数，里面会申请 worker 租约。测试则可以自己提供创建函数，精确控制成功、异常或取消的发生位置，从而隔离真实 ZooKeeper 服务。

虽然这个构造函数是 `internal`，引擎项目通过 `InternalsVisibleTo` 向 `TGame.EngineTests` 开放了内部成员，所以测试程序集能够使用它。这不代表普通业务调用方也能直接使用该构造函数。

## 3. 先理解三个测试辅助方法

### Check：条件不成立就让测试失败

```csharp
private static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}
```

`condition` 是“实际情况是否符合预期”，`message` 是失败说明。条件为真时，方法正常结束；条件为假时，抛出异常。异常会让当前检查失败并向调用方传播。

例如 `Check(!invoked, "Pre-cancellation allocated a generator.")` 要求 `invoked` 必须为假。这里 `!` 是逻辑取反。

### Expect<T>：某个操作应该抛出指定类型的异常

```csharp
private static void Expect<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new Exception($"Expected {typeof(T).Name}.");
}
```

`T` 是泛型参数，调用时可以替换成 `InvalidOperationException` 等具体类型。`where T : Exception` 是约束，要求这个类型必须是 `Exception` 或其派生类型。

`Action` 表示一个没有参数、没有返回值的操作。把操作作为参数传入，辅助方法就可以在自己的 `try` 中执行它，并观察异常。

例如：

```csharp
Expect<InvalidOperationException>(() => failed.GenId());
```

`()` 表示这个 lambda 没有参数；`=>` 后面是执行内容。这里把“调用 `failed.GenId()`”交给 `Expect`，等辅助方法调用 `action()` 时才执行。虽然 `GenId()` 返回字符串，作为 `Action` 使用的这段调用会丢弃返回值。

执行结果分为三种：

| 实际执行结果 | 辅助方法的行为 |
| --- | --- |
| 抛出 `T` 或其派生异常 | `catch (T)` 捕获，直接返回，检查通过 |
| 抛出不匹配的异常 | 不被这个 `catch` 捕获，向外传播，检查失败 |
| 没有抛异常 | 执行最后的 `throw`，报告没有出现预期异常 |

因此，在这些用例中，某些操作抛异常恰好说明状态保护生效。`catch (T)` 接受派生类型，并不检查异常的精确运行时类型、消息或对象身份。

最后一行的 `$"..."` 是字符串插值，`typeof(T).Name` 取得预期类型的名字。例如预期异常未出现时，会得到 `Expected InvalidOperationException.`。

### ExpectAsync<T>：等待异步操作后再判断异常

```csharp
private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }

    throw new Exception($"Expected {typeof(T).Name}.");
}
```

这里使用 `Func<Task>`，表示一个没有参数、返回 `Task` 的函数。例如 `() => failed.InitializeAsync(stop)` 返回初始化任务。

`await action()` 先调用函数取得任务，再等待任务结束。异步操作的异常可能存放在返回的失败任务中，等待时才会传播出来。如果只调用 `action()` 而不等待，就不能可靠地验证异步失败。

调用方同样需要 `await ExpectAsync<T>(...)`，这样当前场景检查完毕后，才会继续下一个场景。

## 4. 给可取消操作设置共同的等待期限

```csharp
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));
var stop = deadline.Token;
```

`CancellationTokenSource` 是发出取消请求的对象，`Token` 是交给其他操作观察的取消令牌。构造时传入 5 秒，表示从这个取消源创建开始，约 5 秒后发出取消请求。

后面把 `stop` 交给初始化或等待操作；这些操作观察到取消后，可以停止等待并抛出取消异常。每个场景没有重新获得独立的 5 秒期限。

`using var` 表示离开变量所在作用域时调用 `Dispose()`，清理取消源持有的资源。它不会在声明后的下一行就释放。

取消是协作式的：它不会强制终止整个方法或线程。后面的同步 `Parallel.For` 没有接收这个 token，`GenId()` 也没有取消参数，因此不能把这里理解成整个测试的硬性 5 秒上限。

## 5. 初始化失败后，服务仍然不能使用

```csharp
await using var failed = new IdGenerator(_ => Task.FromException<SnowflakeIdGenerator>(new IOException("Allocation failed.")));
Expect<InvalidOperationException>(() => failed.GenId());
Expect<InvalidOperationException>(() => _ = failed.LeaseLost);
await ExpectAsync<IOException>(() => failed.InitializeAsync(stop));
Expect<InvalidOperationException>(() => failed.GenId());
```

第一行传入的创建函数是：

```csharp
_ => Task.FromException<SnowflakeIdGenerator>(new IOException("Allocation failed."))
```

参数位置的 `_` 表示这里不使用传入的取消令牌。`Task.FromException<SnowflakeIdGenerator>` 创建一个已经失败的任务：它原本应该在成功时提供 `SnowflakeIdGenerator`，但现在携带的是指定的 `IOException`。

这不会启动后台线程，也不会执行真实 IO。测试只是选择 `IOException` 来模拟创建失败。构造 `failed` 时保存函数，调用初始化时才执行函数并等待这个失败任务。

初始化前的两条检查分别触发 `GenId()` 和 `LeaseLost` 的访问。它们都会经过 `IdGenerator.Current`，而此时内部 `generator` 还是 `null`，所以应抛出 `InvalidOperationException`。

`_ = failed.LeaseLost` 是一个容易忽略的语法点：读取属性会执行 getter，`_ =` 则把返回值丢弃。测试只关心读取时是否抛异常，不需要保存任务。这里的丢弃赋值和前面“未使用的 lambda 参数”用途不同。

然后 `ExpectAsync<IOException>` 调用初始化。`await create(stop)` 观察到失败，异常向外传播；后面把结果写入 `generator` 的语句不会执行。初始化方法的 `finally` 仍会释放生命周期信号量。

最后再次调用 `failed.GenId()`，验证失败之后仍不能发号。对应的过程是：

```text
构造外层服务，内部 generator 为空
  → 初始化前访问被拒绝
  → 初始化执行创建函数
  → 创建任务失败，初始化传播异常
  → generator 没有被赋值，发号仍被拒绝
```

`await using var failed` 保证离开 `RunAsync` 的作用域时等待外层服务的 `DisposeAsync()`。即使初始化失败，也要结束这个外层对象的生命周期。

## 6. 创建结果返回时已被取消，需要先清理资源

```csharp
using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop);
var abandoned = new SnowflakeIdGenerator(WorkerId, TimeProvider.System);
await using var interrupted = new IdGenerator(_ =>
{
    cancelled.Cancel();
    return Task.FromResult(abandoned);
});
await ExpectAsync<OperationCanceledException>(() => interrupted.InitializeAsync(cancelled.Token));
Expect<ObjectDisposedException>(() => abandoned.GenId());
Expect<InvalidOperationException>(() => interrupted.GenId());
```

`CreateLinkedTokenSource(stop)` 创建关联的取消源。共享的 `stop` 被取消时，它也会取消；测试还可以主动调用 `cancelled.Cancel()`。反过来，取消这个关联源不会取消原来的 `stop`，后续场景仍能使用原令牌。

`abandoned` 是提前创建的本地生成器。创建函数故意先发出取消请求，再通过 `Task.FromResult(abandoned)` 返回它。`Task.FromResult` 创建一个已经成功完成的任务，结果就是现有对象，不会克隆生成器，也不启动额外线程。

这个场景模拟的是：创建函数已经拿到资源，即使取消到达，仍然把资源作为成功结果返回。外层初始化必须处理这种情况，不能只依靠创建函数配合取消。

对照 `IdGenerator.InitializeAsync`：

```csharp
var initialized = await create(stop).ConfigureAwait(false);
if (stop.IsCancellationRequested)
{
    await initialized.DisposeAsync().ConfigureAwait(false);
    stop.ThrowIfCancellationRequested();
}

Volatile.Write(ref generator, initialized);
```

执行顺序是：先得到生成器，检查到取消，等待它释放，再抛出取消异常。因此不会执行最后的 `Volatile.Write`，也不会把这个对象发布为可用的生成器。

这里的“发布”只是把对象引用写入外层服务的字段，使后续调用能读到它，与向 ZooKeeper 发布服务地址不是一件事。

三个断言各有用途：

| 断言 | 验证的事情 |
| --- | --- |
| 初始化抛 `OperationCanceledException` | 取消没有被错误地当作初始化成功 |
| `abandoned.GenId()` 抛 `ObjectDisposedException` | 已经返回的底层对象被清理，不能继续使用 |
| `interrupted.GenId()` 抛 `InvalidOperationException` | 外层仍未初始化，没有接纳被取消的结果 |

只验证初始化抛取消异常是不够的：如果没有后两项，无法观察资源是否被释放，以及外层有没有错误地保存它。

## 7. 开始前已经取消，创建函数根本不应该执行

```csharp
var invoked = false;
await using var preCancelled = new IdGenerator(_ =>
{
    invoked = true;
    return Task.FromResult(new SnowflakeIdGenerator(WorkerId, TimeProvider.System));
});
await ExpectAsync<OperationCanceledException>(() => preCancelled.InitializeAsync(cancelled.Token));
Check(!invoked, "Pre-cancellation allocated a generator.");
```

这里复用上一段的 `cancelled.Token`。上一段已经调用过 `Cancel()`，所以这次初始化开始前，令牌就处于取消状态。

`invoked` 用来记录创建函数是否执行。lambda 引用并修改了外部局部变量，这叫捕获外部变量，是闭包的用法。函数执行后，这个标记就会变成 `true`。

初始化的第一步是：

```csharp
await lifecycle.WaitAsync(stop).ConfigureAwait(false);
```

因为令牌已经取消，等待以取消结束，后面的创建函数不会执行。因此测试既要求出现取消异常，也要求 `invoked` 保持为假。

这与上一个场景一起覆盖两个不同位置：开始前取消，应避免分配；创建函数已经返回对象但发现取消，应负责清理。它们并没有穷尽所有可能的线程竞争时序。


## 8. 正常初始化后，拒绝重复初始化

```csharp
var generator = new SnowflakeIdGenerator(WorkerId, TimeProvider.System);
var ids = new IdGenerator(_ => Task.FromResult(generator));
await using (ids)
{
    await ids.InitializeAsync(stop);
    await ExpectAsync<InvalidOperationException>(() => ids.InitializeAsync(stop));
```

这次创建函数返回正常对象。第一次初始化将它保存为内部生成器；第二次初始化检测到 `generator is not null`，应抛 `InvalidOperationException`，避免重复初始化和替换正在使用的对象。

`await using (ids)` 表示退出这个代码块时等待 `ids.DisposeAsync()`。即使块内发生异常，也会尝试释放。

它和前面的声明形式区别在释放时机：

| 形式 | 自动释放时机 |
| --- | --- |
| `await using (ids) { ... }` | 退出这个代码块时 |
| `await using var failed = ...;` | 退出声明所在的作用域时，这里是 `RunAsync` |
| `using var deadline = ...;` | 同样在退出作用域时，但调用同步的 `Dispose()` |

`using` 出现在文件顶部时用于引入命名空间；这里的 `using` 用于管理资源生命周期，要根据位置区分。

## 9. 并发生成 5000 个 ID，怎样判断重复

仍在上面的 `await using` 块中：

```csharp
var generated = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
Parallel.For(0, ConcurrentIds, _ => Check(generated.TryAdd(ids.GenId(), 0), "Shared generator produced duplicate IDs."));
```

`ConcurrentDictionary<string, byte>` 是可供并发访问的字典。键是 ID 字符串，值是一个 `byte`。这里值固定为 `0`，没有业务含义；测试把字典当作“已经出现过的 ID 集合”使用。

`StringComparer.Ordinal` 按字符序号比较字符串，不依赖语言文化规则，适合比较这种精确的标识符。

核心调用 `generated.TryAdd(ids.GenId(), 0)` 先生成 ID，再尝试把它作为键加入字典。没有相同键时添加成功，返回 `true`；已有相同键时返回 `false`。

为了看清执行顺序，可以把循环体理解成下面的等价展开：

```csharp
string id = ids.GenId();
bool added = generated.TryAdd(id, 0);
Check(added, "Shared generator produced duplicate IDs.");
```

只要某次出现重复，`added` 就为假，`Check` 抛异常，这组并行检查失败。`TryAdd` 将“检查键和添加键”作为原子操作处理，避免测试自身发生先查询、再写入的竞争。

`Parallel.For(0, ConcurrentIds, ...)` 执行索引 `0` 到 `4999` 的迭代。运行时可以把迭代安排到多个线程，但不代表创建 5000 个线程，也不保证所有迭代同时执行。参数 `_` 表示不使用循环索引。

每次迭代使用的都是同一个 `ids`，因此检查的是共享同一生成器时的并发发号。底层 `SnowflakeIdGenerator.GenId()` 用 `lock (sync)` 保护序列状态；并发字典则保护测试收集结果的过程。两者分别负责被测对象和检测工具的并发安全。

`Parallel.For` 会等这些迭代结束后才返回，所以不会在这些调用尚未结束时正常退出 `await using` 块。这组用例没有同时发起 `GenId()` 与 `DisposeAsync()` 来测试二者竞争。

检查通过能说明本次 5000 次调用没有重复，不能仅凭这一项就推导出所有时间边界或跨进程场景都已验证。算法边界可以继续对照 [IdGeneratorChecks.cs](IdGeneratorChecks.cs.md)。

## 10. 退出作用域后，外层和内层都不能再使用

```csharp
Expect<ObjectDisposedException>(() => generator.GenId());
Expect<ObjectDisposedException>(() => ids.GenId());
await ids.DisposeAsync();
```

执行到这里时，前面的 `await using (ids)` 已经完成异步释放。

第一条断言访问底层 `generator`，验证外层释放时确实释放了内部对象。第二条访问外层 `ids`，验证服务入口本身也拒绝已释放后的使用。

释放不会让变量立即消失，变量仍然引用这个对象。因此可以继续调用方法，观察它是否按约定报告已释放状态。

第三句再次调用 `DisposeAsync()`，检查重复释放安全。实现中的 `if (disposed) { return; }` 会让已经释放的对象直接结束清理流程。这种重复操作不再改变结果的行为称为幂等性。

## 11. 新实例独立工作，原样暴露失效任务

```csharp
var session = new ZooKeeperSession();
session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
var next = new SnowflakeIdGenerator(WorkerId, TimeProvider.System, session: session);
await using var nextIds = new IdGenerator(_ => Task.FromResult(next));
await nextIds.InitializeAsync(stop);
Check(IdText.IsValid(nextIds.GenId()), "The next instance did not initialize its generator.");
Check(ReferenceEquals(nextIds.LeaseLost, next.LeaseLost), "The service hid the active generator's lease supervision.");
await ids.DisposeAsync();
Check(IdText.IsValid(nextIds.GenId()), "Disposing another instance invalidated the active generator.");
```

这次对象关系为 `nextIds → next → session`。测试直接调用 `StateChanged(SyncConnected)`，把会话置于已连接状态，并未通过网络连接 ZooKeeper。

`session: session` 是命名参数：冒号左边是构造函数参数名，右边是传入的局部变量。它明确指出当前传入的是可选的会话参数。

初始化后，`IdText.IsValid(nextIds.GenId())` 先发号，再检查结果的字符串格式。`IdText` 要求非空、无前导零，并能按指定格式解析为正 `long`；这项检查不负责跨实例唯一性。见 [IdText.cs](../../server/engine/identifiers/IdText.cs.md)。

`ReferenceEquals(nextIds.LeaseLost, next.LeaseLost)` 比较两个引用是否指向同一个任务对象。它不只是比较任务当前是否完成，而是确认外层服务直接暴露底层生成器的失效通知：

```csharp
public Task LeaseLost => Current.LeaseLost;
```

这个表达式属性在每次读取时取得当前生成器的 `LeaseLost`。调用方由此可以通过 `IdGenerator` 等待底层失效事件。

随后再次释放的是旧实例 `ids`，检查还能发号的是新实例 `nextIds`。这验证旧对象的释放没有破坏新对象。

应用中把 `IdGenerator` 注册为 IoC 单例，表示同一个容器共享这个服务实例；类本身仍可以创建多个对象。这个测试要求它们分别管理自己的状态，而不是通过静态字段共享一个生成器或已释放标记。

这里新旧生成器使用了相同的测试 worker 编号，但没有比较它们生成的 ID 集合。这个用例关注生命周期隔离，不能据此推导生产中多个生成器可以随意共享 worker 编号。

## 12. 会话断开后停止发号，并且不能自行恢复

```csharp
session.StateChanged(Watcher.Event.KeeperState.Disconnected);
await nextIds.LeaseLost.WaitAsync(stop);
Expect<InvalidOperationException>(() => nextIds.GenId());
session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
Expect<InvalidOperationException>(() => nextIds.GenId());
```

第一句模拟断线。这个会话已经收到过连接成功事件，因此 [ZooKeeperSession.cs](../../server/engine/ZooKeeperSession.cs.md) 会调用 `Invalidate()`：

```csharp
failed = true;
connected = false;
Connected.TrySetResult();
Lost.TrySetResult();
```

`failed` 记录这个会话已经失效，`connected` 表示当前不再连接。`Lost` 是任务完成源，调用 `TrySetResult()` 会尝试把它对应的 `Task` 标记为成功完成。这里任务成功完成表达的是“失效事件已经发生”，不代表租约有效。

`nextIds.LeaseLost` 指向这个失效任务。`WaitAsync(stop)` 返回一个可以因取消令牌而结束等待的任务，外面的 `await` 等待它。若失效事件一直不来，共享令牌取消可以结束此次等待；取消等待不会撤销租约，也不会把原始 `LeaseLost` 任务变成已取消。

在当前顺序中，`StateChanged` 已经完成 `Lost`，所以随后等待通常立即完成。任务也能表达“事件过去已经发生”，不会因为先发通知、后开始等待而漏掉它。

之后调用 `GenId()` 会执行 `session?.CheckActive()`。这里 `?.` 表示只有 `session` 非空时才调用；本场景传入了会话，所以检查会执行。会话失效时抛出 `InvalidOperationException`，发号被拒绝。

为什么又模拟连接成功，发号仍然失败？因为会话只在下面的条件成立时接受连接成功：

```csharp
if (state == Event.KeeperState.SyncConnected && !failed)
```

前面的断线已经把 `failed` 设置成 `true`，这个历史失效状态不会被后来的连接事件清除。因此旧生成器继续拒绝发号。

这是项目的身份安全策略：已经连接过的会话一旦断开，就停止信任当前身份，不凭一次重连通知恢复旧生成器。它不等同于 ZooKeeper 协议规定所有短暂断线都会立即让会话过期；生产恢复需要按项目流程重新获取身份。

## 13. 最后的释放、通过提示与自动清理

```csharp
await nextIds.DisposeAsync();
Expect<ObjectDisposedException>(() => next.GenId());
Expect<ObjectDisposedException>(() => nextIds.GenId());
```

即使会话已经失效，外层服务仍需要释放。释放后分别检查内外两层均不能发号。

此时异常从前面的 `InvalidOperationException` 变成了 `ObjectDisposedException`：前者表达会话状态使发号不可用，后者更具体地表达对象已经结束生命周期。两个类型在继承关系上并非互不相关，`ObjectDisposedException` 本身继承自 `InvalidOperationException`；测试直接写出已释放异常，是为了检查更具体的结果。

`nextIds` 前面使用 `await using var` 声明，所以退出方法时还会自动再调用一次 `DisposeAsync()`。重复释放安全，使显式释放后的状态检查和作用域自动清理可以一起使用。

最后的 `Console.WriteLine("PASS process ID instances: ...")` 表示程序已经走过前面的显式检查。之后退出作用域时，还会按声明的逆序释放 `await using var` 和 `using var` 管理的对象。

因此，看到这一行输出不应忽略随后清理是否出错；完整执行成功还要求 `RunAsync()` 正常返回。本文描述源码预期，没有因此声称本轮实际运行过测试。

本文件直接构造本地生成器并注入会话事件，没有申请真实 worker 租约。真实临时节点的分配、竞争和删除要结合 [IdGeneratorChecks.cs](IdGeneratorChecks.cs.md) 与 [ZooKeeperProxyChecks.cs](ZooKeeperProxyChecks.cs.md) 的对应集成场景阅读。
