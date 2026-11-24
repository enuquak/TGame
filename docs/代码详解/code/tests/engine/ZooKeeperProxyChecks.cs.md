# ZooKeeperProxyChecks.cs 代码详解

源码：[code/tests/engine/ZooKeeperProxyChecks.cs](../../../../../code/tests/engine/ZooKeeperProxyChecks.cs)

这个文件分成两组检查：`CheckSessionAsync` 验证初始化保护和本地会话状态；`RunAsync` 连接真实 ZooKeeper，验证进程占号、地址发布、发现通知和编号复用。两组都检查代理，但准备环境和证明范围不同。

阅读本文件不需要执行测试。本文解释源码的预期行为，没有声称本轮运行过检查。实现对应 [ZooKeeperProxy.cs](../../server/engine/ZooKeeperProxy.cs.md) 和 [ZooKeeperSession.cs](../../server/engine/ZooKeeperSession.cs.md)。

## 1. 固定身份、地址和空观察者分别做什么

```csharp
private static readonly ServerProcess Process = new(ProcessType.Router, 1);
private const string Address = "http://127.0.0.1:12345";
private const string ReplacementAddress = "http://127.0.0.1:12346";
private const int TimeoutSeconds = 10;
```

`Process` 是测试使用的 `Router1` 身份。`static readonly` 表示类级共享、初始化后不替换这个字段；`new(...)` 的类型由左边推断成 `ServerProcess`。

两个地址用于验证数据有没有被覆盖、地址变化是否触发发现。它们在本文件中是 ZooKeeper 节点中的测试数据，不代表这里真的启动了监听在这两个端口的业务服务器。

嵌套的观察者写为：

```csharp
private sealed class WatcherStub : Watcher
{
    public override Task process(WatchedEvent evt) => Task.CompletedTask;
}
```

它满足实际客户端构造函数需要观察者的接口，但不记录或处理事件。这个原始客户端主要作为测试中的“另一位占号者”和节点修改者。返回 `Task.CompletedTask` 表示回调已经处理完，不是建立连接成功的证明。

`LoadOptions()` 调用 `ServerConfiguration.LoadSelected()` 后取出 `ZooKeeperOptions`，所以真实集成检查仍使用统一选择的完整配置，不自己拼一份生产连接参数。

## 2. 先认识断言：抛异常也可能是检查通过

`Check(condition, message)` 要求条件为真，否则抛出带说明的异常。例如 `Check(process.Name == "Router2", ...)` 验证实际结果是不是期望身份。

`Expect<T>` 接收 `Action`，即无参数、无返回值的操作。它在 `try` 中执行操作，捕获 `T` 时返回；没有抛异常则在最后主动报错。抛出其他类型异常则继续向外传播。

`ExpectAsync<T>` 接收 `Func<Task>`，即无参数、返回任务的函数。它的核心区别是：

```csharp
try
{
    await action();
}
catch (T)
{
    return;
}
```

必须等待任务，才能观察保存于失败任务中的异步异常。调用方也必须等待 `ExpectAsync` 完成，才能确定当前断言已经检查完。

`where T : Exception` 限制泛型参数必须是异常类型；`catch (T)` 接受该类型及其派生类型，不比较异常消息。`typeof(T).Name` 用于没有出现预期异常时给出类型名。

`Expect<InvalidOperationException>(() => _ = proxy.Process)` 中，lambda 延迟执行属性读取，`_ =` 丢弃结果但保留 getter 的执行。`Expect<InvalidOperationException>(initial.CheckActive)` 则直接传入方法组，编译器把这个无参方法转换成 `Action`。

## 3. 构造错误端口，但先验证未初始化保护

```csharp
var options = TestServices.LocalZooKeeperOptions() with { Endpoint = "127.0.0.1:invalid-port" };
await using var proxy = new ZooKeeperProxy(options, log);
Expect<InvalidOperationException>(() => _ = proxy.Process);
Expect<InvalidOperationException>(() => _ = proxy.SessionLost);
await ExpectAsync<InvalidOperationException>(() => proxy.IsProcessClaimedAsync("Router1", CancellationToken.None));
```

`with` 复制原有配置并修改一个属性，便于只改变被测条件。这里故意把端口写成无法解析的文字。

为什么构造 `ZooKeeperProxy` 没有立即报端口错误？构造函数只检查地址非空、路径和超时，没有创建真正的 ZooKeeper 客户端。端口解析在初始化创建客户端时才发生。

此时读取 `Process`、读取 `SessionLost`、查询身份，都应因尚未初始化或会话不活跃而被拒绝。`CancellationToken.None` 表示不主动给这次调用设置取消条件，不表示忽略方法自己的状态保护。

`await using var proxy` 让退出方法作用域时自动等待代理释放，即使中间断言失败也会尝试清理。

## 4. 过早认领不能误释放代理

```csharp
await using var earlyClaim = new ZooKeeperProxy(options, log);
await ExpectAsync<InvalidOperationException>(() => earlyClaim.ClaimProcessAsync(ProcessType.Router, CancellationToken.None));
// 过早认领应只拒绝调用；之后初始化仍应进入地址解析，而非实例已被释放。
await ExpectAsync<FormatException>(() => earlyClaim.InitializeAsync());
```

首先在未初始化时认领，应得到非法操作异常。随后执行初始化，预期能走到真实客户端的地址解析，并因 `invalid-port` 抛 `FormatException`。

这个第二断言很关键。如果过早认领错误地关闭了整个代理，后续初始化可能先报对象已释放，而不会进入端口解析。它把“拒绝不合时机的调用”和“使对象永久不可用”区分开。

实现中 `ClaimProcessAsync` 的 `Client` 和会话检查位于申请的清理 `try` 之前，正是为这条行为服务。

## 5. 用本地端口制造“客户端已经创建，但握手尚未完成”

```csharp
var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
listener.Start();
var writeZkLog = ZooKeeper.LogToFile;
ZooKeeper.LogToFile = false;
using var pendingStop = new CancellationTokenSource();
await using var pending = new ZooKeeperProxy(options with { Endpoint = listener.LocalEndpoint.ToString()! }, log);
var initializing = pending.InitializeAsync(pendingStop.Token);
```

`TcpListener` 在本机回环地址监听，端口 0 让操作系统选择一个可用端口。`Start()` 后从 `LocalEndpoint` 取出实际地址，放入新的选项副本。

这个监听器没有实现 ZooKeeper 协议握手，只是提供一个本地 TCP 监听端口，因此测试可以观察“建立客户端对象不等于完成 ZK 初始化”的阶段。这里不需要真实 ZooKeeper 服务器。

`ToString()!` 的 `!` 只处理编译器的可空分析，不负责启动监听或验证地址。

测试先保存库的全局日志开关，再临时关闭文件日志，最后必须恢复，避免这个场景影响后续检查。

`var initializing = pending.InitializeAsync(...)` 启动初始化并保存任务，故意不在这里立即 await。这样下一句断言才能在握手没有成功时观察代理：

```csharp
Expect<InvalidOperationException>(() => _ = pending.SessionLost);
```

它要求即使内部 `zk` 已被创建，对外的 `SessionLost` 仍不能暴露为已初始化服务的入口。这也解释了代理为什么另外保存 `initialized`，不能只判断客户端引用是否为空。

## 6. pending 场景的嵌套 finally 为什么这样写

```csharp
finally
{
    pendingStop.Cancel();
    try
    {
        await ExpectAsync<OperationCanceledException>(() => initializing);
    }
    finally
    {
        listener.Stop();
        ZooKeeper.LogToFile = writeZkLog;
    }
}
```

外层 `finally` 保证无论前面的 getter 断言怎样结束，都会发出取消。随后等待之前保存的初始化任务，确认它以取消结束，避免留下一个没有观察结果的后台操作。

`() => initializing` 不会重新调用初始化，它只返回已有任务。

内层 `finally` 保证即使取消结果不符合预期，监听器仍停止、日志开关仍恢复。最后退出作用域时，`pending` 和取消源还会执行各自的自动释放。

这段检查的是正在初始化时的取消清理，不能简化为“调用了 Cancel，所以所有清理一定成功”；等待结果和嵌套 finally 各自承担了一部分保证。

## 7. 预取消和非法配置分别在哪里被拒绝

```csharp
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
await ExpectAsync<OperationCanceledException>(() => proxy.InitializeAsync(cancelled.Token));
Expect<ArgumentException>(() => new ZooKeeperProxy(options with { DiscoveryRoot = "/bad//root" }, log));
Expect<ArgumentOutOfRangeException>(() => new ZooKeeperProxy(options with { SessionTimeoutMilliseconds = 0 }, log));
Expect<InvalidOperationException>(() => _ = proxy.Process);
```

预取消发生在调用前，初始化获取生命周期许可时就会观察到取消，不应进入真实连接流程。

`/bad//root` 有空路径段，应被本地路径校验拒绝；会话超时为 0 应被正数校验拒绝。这两条是在构造代理时验证参数，不是通过服务端拒绝来证明。

最后读取 Process 仍失败，说明前面没有把最初的 proxy 变成已认领身份的服务。

## 8. 初次未连接与已经连接后的断开是两种状态

```csharp
var initial = new ZooKeeperSession();
initial.StateChanged(Watcher.Event.KeeperState.Disconnected);
Expect<InvalidOperationException>(initial.CheckActive);
Check(!initial.Lost.Task.IsCompleted, "An initial connection attempt must not lose an unestablished session.");
initial.StateChanged(Watcher.Event.KeeperState.SyncConnected);
initial.CheckActive();
initial.Invalidate();
Expect<InvalidOperationException>(initial.CheckActive);
```

刚创建时 `connected` 和 `failed` 都是假。首次连接尚未成功就收到 Disconnected，仍不能使用，但不应永久失效，因此 `Lost` 还不能完成。

之后收到 SyncConnected，`CheckActive` 正常返回，说明首次连接仍有成功机会。主动调用 Invalidate 后，再次检查应失败。

这条用例用同一个会话对象展示了三个阶段：尚未连接、有效连接、明确失效。不能把 `!connected` 直接等同于“这个对象已经失败，永远不能连接”。

## 9. 三种终止事件和认证失败的唤醒

循环依次测试 `Disconnected`、`Expired` 和 `AuthFailed`。每轮都重新创建会话，先连接成功，再注入目标事件：

```csharp
session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
session.CheckActive();
session.StateChanged(state);
Check(session.Lost.Task.IsCompletedSuccessfully, "A failed session must notify all users.");
Expect<InvalidOperationException>(session.CheckActive);
session.StateChanged(Watcher.Event.KeeperState.SyncConnected);
Expect<InvalidOperationException>(session.CheckActive);
```

每轮的断言分别检查通知已发出、会话不可使用、后来的连接成功事件也不能恢复旧对象。这个永久失效行为是当前项目策略，不是对 ZooKeeper 一切短暂断线的协议结论。

另一条用例在从未连接成功时直接注入认证失败：

```csharp
var rejected = new ZooKeeperSession();
rejected.StateChanged(Watcher.Event.KeeperState.AuthFailed);
Check(rejected.Connected.Task.IsCompletedSuccessfully, "Authentication failure must unblock initialization.");
Expect<InvalidOperationException>(rejected.CheckActive);
```

`Connected` 任务完成，使初始化不再等待；紧跟着 `CheckActive` 失败，证明任务完成并不等于业务连接成功。完成源只负责发信号，最终可用性由状态检查决定。

`CheckSessionAsync` 到这里打印通过提示。退出方法时还要完成自动释放，因此完整成功仍以方法正常返回为准。


## 10. RunAsync：真实 ZooKeeper 场景怎样隔离数据

```csharp
var configured = LoadOptions();
var options = configured with { DiscoveryRoot = configured.DiscoveryRoot + "/dynamic-check-" + Guid.NewGuid().ToString("N") };
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
var stop = deadline.Token;
var owner = new ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, new WatcherStub());
await using var proxy = new ZooKeeperProxy(options, log);
await proxy.InitializeAsync(stop);
```

`configured` 来自所选完整配置。`with` 为本次检查生成新的 DiscoveryRoot，追加 `dynamic-check-` 和无连字符的 GUID 文本，避免直接在正常发现根下操作业务成员。

这里有两个不同的客户端：`owner` 直接创建、修改节点，用来模拟另一个进程；`proxy` 是被测试的封装。它们有各自的会话，因此可以检验一个客户端是否会覆盖另一个客户端的占号。

10 秒取消源用于传入 stop 的主要测试步骤，不是每一步都重新获得 10 秒。和其他取消令牌一样，它不能强制终止没有观察它的清理操作。

准备好两个节点路径：一个是 `server-Router1`，另一个是 `server-Scene1`。下面的 `try/finally` 负责进入该块之后的清理；原始 owner 创建和 proxy 初始化位于这个 try 之前，所以不能把 finally 描述为保护了方法内所有准备阶段的失败。

## 11. 先由 owner 占 Router1，再让代理认领

测试逐段创建持久父目录。循环把 `/a/b/c` 分解为 `/a`、`/a/b`、`/a/b/c` 逐个尝试创建，遇到 NodeExists 就继续，不把已有公共父目录当作冲突。

然后执行：

```csharp
await owner.createAsync(path, Encoding.UTF8.GetBytes(Address), ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(stop);
var process = await proxy.ClaimProcessAsync(ProcessType.Router, stop);
Check(process.Name == "Router2", "An occupied number must be skipped without overwriting its owner.");
var original = await owner.getDataAsync(path).WaitAsync(stop);
Check(Encoding.UTF8.GetString(original.Data) == Address, "Claim changed another process's address.");
```

`owner` 创建 Router1 临时节点并写入地址。`proxy` 从编号 1 开始尝试，遇到已占用后必须转向 Router2。

第一个断言检查选择了下一个编号；第二个重新读取 Router1 的数据，检查占号过程没有改写别人已经发布的地址。只检查结果名字，还不能直接证明原节点数据没有被破坏，因此这里保留了第二项。

随后两条异常断言分别要求：同一个代理不能再次认领身份，也不能拿静态的 Router1 身份去发布地址。代理真正认领的是 Router2，所以后者必须被所有权检查拒绝。

## 12. 为什么手动取得异步枚举器

```csharp
await using (var discovery = proxy.WatchProcessesAsync(stop).GetAsyncEnumerator(stop))
{
    Check(await discovery.MoveNextAsync(), "Discovery did not produce its initial snapshot.");
    Check(discovery.Current.Count == 1, "A reserved identity without an endpoint must not be dialed.");
```

`WatchProcessesAsync` 返回异步序列；`GetAsyncEnumerator` 得到控制读取位置的枚举器。每次 `MoveNextAsync()` 都请求下一份快照，返回真表示有结果，随后通过 `Current` 读取它。

这里不用一个持续运行的 `await foreach`，是为了让测试按“修改节点 → 推进枚举 → 检查快照”的顺序控制场景。`await using` 确保退出代码块时异步释放枚举器。

初始快照应该只有一项：owner 的 Router1 已写地址；proxy 的 Router2 虽然占号成功，但数据仍为空，必须被发现层过滤。因此 `Count == 1` 验证占号与可拨号地址的区别。

接着发布 Router2：

```csharp
await proxy.RegisterProcessAsync(process, ReplacementAddress, stop);
await Until(items => items.Any(peer => peer.Process.Name == "Router2"));
```

发布只修改子节点数据，不会给父节点增加新孩子。能观察到这次变化，依赖代理给子节点注册的数据 Watch，不能只靠父节点的成员增删观察。

## 13. Until：不断读快照，直到谓词成立

局部辅助函数写在枚举块后部，但可以在其作用域内提前调用：

```csharp
async Task Until(Func<IReadOnlyList<DiscoveredProcess>, bool> predicate)
{
    do
    {
        Check(await discovery.MoveNextAsync(), "Discovery ended before a member change.");
    }
    while (!predicate(discovery.Current));
}
```

`Func<IReadOnlyList<DiscoveredProcess>, bool>` 要求一个接收快照、返回布尔值的判断函数。例如 `items => items.Any(...)` 表示快照中至少有一个满足条件的成员。

局部函数捕获外面的 `discovery`，所以不必每次显式传枚举器。它不是新建一个发现订阅，而是继续推进现有枚举。

`do ... while` 至少先读一次新快照，再检查谓词；即使上一份 Current 恰好已经满足，也不会只检查旧值就返回。若本轮通知只反映了部分中间变化，谓词不满足时继续读取。

正常退出要求谓词最终为真；枚举提前结束会由 Check 报错，持续等不到目标状态则受发现序列的取消令牌限制。这个函数没有自己设置新的独立超时。

## 14. 非法名字、成员加入、同名重建、地址修改与离开

先创建 `server-Unknown1`，再等待快照数量仍为 2。这里的 Unknown 不在有效进程类型集合中，代理应忽略它而保留健康的 Router1、Router2。该断言要求有效列表不被这条记录扩充。

之后创建 Scene1 并等待它出现：

```csharp
await owner.createAsync(scenePath, Encoding.UTF8.GetBytes(Address), ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(stop);
await Until(items => items.Any(peer => peer.Process.Name == "Scene1"));
long generation = discovery.Current.Single(peer => peer.Process.Name == "Scene1").Generation;
```

`Any` 只判断是否存在；`Single` 要求恰好有一个匹配元素，然后取出它的 Generation。匹配不到或出现多项时，Single 会报错，而不会悄悄选择第一条。

接着删除并立刻重建同一路径：

```csharp
await owner.deleteAsync(scenePath).WaitAsync(stop);
await owner.createAsync(scenePath, Encoding.UTF8.GetBytes(Address), ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(stop);
await Until(items => items.Any(peer => peer.Process.Name == "Scene1" && peer.Generation != generation));
```

名字和地址保持相同，但新节点的创建事务标识不同，因此 Generation 必须变化。这里不要求发现循环一定先看到“暂时不存在”的中间快照；它要求最终识别新代次，适应通知合并和快速重建。

再通过 `setDataAsync` 把地址改为 ReplacementAddress，等待快照中的同名成员地址更新。最后删除 Scene1，使用 `All(peer => peer.Process.Name != "Scene1")` 检查它已离开列表。

`All` 要求每个元素都满足条件，空列表时也为真。因此最后这一项证明没有 Scene1，但单靠它没有额外证明其他每个成员都仍存在。阅读测试时不要把断言范围扩大。

## 15. 释放后，旧通知完成，新实例可以复用编号

```csharp
var lost = proxy.SessionLost;
await proxy.DisposeAsync();
Check(lost.IsCompletedSuccessfully, "Disposal must invalidate the session.");
await owner.deleteAsync(path).WaitAsync(stop);
await using var next = new ZooKeeperProxy(options, log);
await next.InitializeAsync(stop);
var reclaimed = await next.ClaimProcessAsync(ProcessType.Router, stop);
Check(reclaimed.Name == "Router1", "A released number should be reusable.");
await next.RegisterProcessAsync(reclaimed, Address, stop);
Check(await next.IsProcessClaimedAsync(reclaimed.Name, stop), "Published identity is not discoverable.");
```

先保存 `SessionLost` 的任务引用，再释放 proxy，是因为释放后再次读取该属性会经过 Client 的已释放检查。已经取得的 Task 引用仍然有效，可以用来观察通知是否完成。

代理释放时使本地会话失效并关闭客户端。owner 再删除自己持有的 Router1 路径，新代理 next 才从编号 1 重新认领。

`reclaimed.Name == "Router1"` 证明当前最低空闲编号能被新实例获得。随后发布地址，再用 `IsProcessClaimedAsync` 验证临时身份存在。该查询不检查真实 RPC 连通性，也不代表地址上的服务器已经接受请求。

next 使用 `await using var` 声明在 try 的作用域内，退出这个作用域时会自动释放，然后进入外层 finally 清理本次测试目录。

## 16. finally 清理什么，为什么不用已取消的 stop

```csharp
finally
{
    await proxy.DisposeAsync();
    // 只清理本检查生成的独立根目录。
    var children = await owner.getChildrenAsync(options.DiscoveryRoot);
    foreach (var child in children.Children)
    {
        await owner.deleteAsync(options.DiscoveryRoot + "/" + child);
    }

    await owner.deleteAsync(options.DiscoveryRoot);
    await owner.closeAsync();
}
```

再次释放 proxy 依赖它的幂等清理逻辑。然后列出本次随机根目录的孩子，逐个删除，再删除根，最后关闭 owner。不会扫描或删除正常发现根下的其他运行数据。

这里没有把已经可能取消的 stop 继续交给清理，所以不会仅因测试期限耗尽就立刻拒绝所有清理等待。不过这也不是统一的清理超时保证：这些调用仍可能失败，失败会向上传播；逐步执行的 finally 不等于每一个后续清理动作在前一步抛异常后仍必定运行。

## 17. 这份测试实际覆盖到哪里

本地检查覆盖代理初始化前/期间的访问保护、预取消、部分配置校验、初次断开与终止失效，以及认证失败唤醒初始化。它通过手工注入会话事件检查状态逻辑，没有模拟真实网络分区后的所有客户端回调时序。

集成检查覆盖进程节点占用、数据不被覆盖、身份发布保护、空地址过滤、动态发现、同名代次替换与进程编号复用。它操作的主要是进程发现路径，没有在这里穷尽雪花 worker 的全部分配与竞争场景；worker 相关部分继续看 [IdGeneratorChecks.cs](IdGeneratorChecks.cs.md)。

测试里的 PASS 输出说明执行到了对应位置，不等于本文已经实际执行测试，也不能代替之后自动清理正常结束的结果。
