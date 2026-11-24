# ZooKeeperProxy.cs 代码详解

源码：[code/server/engine/ZooKeeperProxy.cs](../../../../../code/server/engine/ZooKeeperProxy.cs)

`ZooKeeperProxy` 是进程内共享的 ZooKeeper 服务。它把客户端连接、进程身份占用、地址发布、动态发现和雪花 worker 租约放在同一个会话下管理。这里的 proxy 是项目自己的封装，字段 `zk` 才是第三方库的实际客户端。

先读 [ZooKeeperSession.cs](ZooKeeperSession.cs.md)，再按本文的生命周期和方法顺序阅读。当前应用通过根容器取得一个代理，构造函数不连接网络，宿主显式调用初始化。见 [Program.cs](../app/Program.cs.md) 和 [ServerServices.cs](../app/ServerServices.cs.md)。

## 1. 先看三种路径，分清身份与地址

| 路径示例 | 创建方式 | 记录的内容 |
| --- | --- | --- |
| `/tgame/test`、`/tgame/id-workers` | `PERSISTENT` 持久节点 | 供其他节点使用的父目录 |
| `/tgame/test/server-Router1` | `EPHEMERAL` 临时节点 | 起初为空，占用进程身份；就绪后写入服务地址 |
| `/tgame/id-workers/0` | `EPHEMERAL` 临时节点 | 数据为空，路径名表示 worker 编号被占用 |

ZooKeeper 路径是服务端的节点路径，不是本机文件路径。相同路径的创建由 ZooKeeper 协调，一个调用方成功创建后，另一个创建者会得到节点已存在的结果。

临时节点与创建它的会话关联，会话关闭或过期后由 ZooKeeper 清理。物理断网、客户端收到断线事件和服务端删除节点并不发生在同一个瞬间。当前应用观察到已连接会话断线后，会先在本地拒绝使用旧身份。

进程编号与 worker 编号独立：`Router1` 不必使用 worker 1。进程数量配置也不是这里的编号上限；占号按可用路径递增寻找。

## 2. 字段各自保存什么事实

```csharp
private ZooKeeper? zk;
private readonly GameLog log;
private readonly SemaphoreSlim lifecycle = new(1);
private readonly ZooKeeperOptions options;
private readonly ZooKeeperSession session = new();
private bool disposed;
private volatile bool initialized;
private ServerProcess? claimedProcess;
private int claiming;
```

`ZooKeeper?` 中的 `?` 表示可空引用，创建代理时尚没有客户端。`claimedProcess` 也可能为空，因为连接成功和占到进程身份是两个阶段。

`readonly` 表示构造后不能把字段替换成另一个对象，不表示所引用的对象内部完全不可变。例如 `session` 引用不变，它内部的连接状态仍会更新。

`lifecycle = new(1)` 创建初始许可数为 1 的信号量，初始化和释放进入同一条互斥路径。它可跨 `await` 持有许可，避免这两个流程交错。它不是因为类被注册成单例才自动有用，而是因为同一对象可能收到并发生命周期调用。

`initialized` 表示握手等待和状态检查已经成功，不能直接用 `zk != null` 替代：创建客户端时就赋值 `zk`，那时还没有证明连接成功。`volatile` 约束该字段的跨线程读写，不代表所有字段组合都有一个共同的锁。

`claiming` 记录是否已经开始过一次身份认领，和 `claimedProcess` 表示的成功结果不同。字段默认值为 0，申请入口原子地把它变成 1，之后不重置；同一个代理不允许不断更换进程身份。

## 3. 构造函数只保存依赖，属性负责访问前置条件

```csharp
public ZooKeeperProxy(ZooKeeperOptions options, GameLog log)
{
    ArgumentNullException.ThrowIfNull(options);
    options.Validate();
    this.options = options;
    this.log = log;
}
```

先验证选项，再保存选项和共享日志。没有 `new ZooKeeper(...)`，所以根容器创建服务不等于连接已建立。日志的最终释放归 App，这个代理不会释放注入的 `GameLog`。

`Client` 是私有 getter，每次访问都执行保护逻辑：

```csharp
ObjectDisposedException.ThrowIf(disposed, this);
if (!initialized)
{
    throw new InvalidOperationException("Initialize the process ZooKeeper proxy before use.");
}

return zk!;
```

先拒绝已释放对象，再拒绝未成功初始化。`zk!` 的后缀 `!` 是消除可空警告的表达式，不会在运行时创建客户端或检查空值；这里依靠前面建立的初始化约束。

`SessionLost` 中的 `_ = Client` 是为了执行 getter 的检查，丢弃返回值，然后返回 `session.Lost.Task`。所以只创建客户端但尚未初始化成功时，也不能把监控入口暴露为可用。

`Process` 先调用 `session.CheckActive()`，再通过 `claimedProcess ?? throw ...` 检查身份是否已经认领。`??` 表示左边不为空则使用左边，否则执行右边表达式。连接成功不等于已经有进程编号。

## 4. InitializeAsync：什么时候才算初始化完成

入口先获取生命周期许可：

```csharp
await lifecycle.WaitAsync(stop).ConfigureAwait(false);
try
{
    ObjectDisposedException.ThrowIf(disposed, this);
    if (zk is not null)
    {
        throw new InvalidOperationException("The process ZooKeeper proxy is already initialized.");
    }
```

`WaitAsync(stop)` 可以在等待许可时被取消。它位于 `try` 外面，所以没有取得许可就取消时，不会误执行 `Release()`。取得许可后，无论下面成功还是抛异常，外层 `finally` 都会归还许可。

`ConfigureAwait(false)` 表示后续代码不要求恢复到原来的同步上下文。这里操作服务状态，不访问 UI；它不会把每个调用自动放到一个新线程。

然后创建与调用方取消令牌关联的本地期限：

```csharp
using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
timeout.CancelAfter(options.ConnectTimeoutMilliseconds);
```

调用方取消或配置的期限到达，都可以结束等待。`using var` 在退出作用域时清理取消源，不是声明后立即释放。

真正连接与确认的顺序是：

```csharp
zk = new ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, session);
await session.Connected.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
timeout.Token.ThrowIfCancellationRequested();
session.CheckActive();
initialized = true;
```

构造客户端交给库去连接，`session` 接收回调。随后等待 `Connected`，再次确认取消状态，再确认会话有效，最后才设置 `initialized`。

为什么任务完成后还检查？`ZooKeeperSession.Invalidate()` 也会完成 `Connected`，用来唤醒等待中的初始化。因此任务已完成可能表示成功连接，也可能表示已经失败；只有 `CheckActive()` 通过才能向外宣布可用。

上述创建客户端、等待连接和确认连接结果的步骤失败时，走内部 `catch`：

```csharp
catch
{
    await CloseAsync().ConfigureAwait(false);
    throw;
}
```

清理当前会话后重新抛出异常，不把失败转换成成功。这里调用 `CloseAsync` 而不是再次进入 `DisposeAsync`，因为本方法已经持有 `lifecycle`；后者还要获取同一许可，会形成自己等待自己的问题。

进入这个内部 `catch` 后，对象会进入已释放状态，不能把它当作可重新初始化的空壳，正常重建需要新的对象和会话。但等待生命周期许可时就取消，不会走到这条清理路径；已经初始化的代理拒绝第二次初始化时，也不会因此关闭原有有效会话。要按异常发生的位置判断是否释放，不能把所有初始化调用失败都等同于对象已释放。

## 5. ClaimProcessAsync：占号早于地址发布

入口依次检查进程类型、初始化状态和会话状态：

```csharp
if (!Enum.IsDefined(type))
{
    throw new ArgumentOutOfRangeException(nameof(type));
}

// 尚未建立会话时不进入占号流程，避免误关闭正在初始化的代理。
_ = Client;
session.CheckActive();
if (Interlocked.Exchange(ref claiming, 1) != 0)
{
    throw new InvalidOperationException("This process has already requested an identity.");
}
```

`Enum.IsDefined` 拒绝未声明的枚举值。前面的检查放在申请的清理 `try` 之外：过早调用仅拒绝这次调用，不应误关闭一个尚在初始化的代理。

`Interlocked.Exchange(ref claiming, 1)` 原子地把字段设为 1，并返回旧值。`ref` 表示把字段位置交给方法修改。只有观察到旧值为 0 的调用方能继续；旧值非 0，说明已经有申请发生过。这比先判断再普通赋值能正确处理同时到来的申请。

随后确保发现父路径存在，从编号 1 开始尝试：

```csharp
for (int number = 1; ; number = checked(number + 1))
{
    session.CheckActive();
    stop.ThrowIfCancellationRequested();
    var process = new ServerProcess(type, number);
    // 空地址只占用编号，进程初始化完成后再发布给发现者。
    if (!await TryCreateEphemeralAsync(ProcessPath(process), stop).ConfigureAwait(false))
    {
        continue;
    }

    session.CheckActive();
    claimedProcess = process;
    return process;
}
```

循环中间没有终止条件，表示不断尝试，直到成功、取消或异常。`checked(number + 1)` 要求编号溢出时抛异常，避免整数绕回。`continue` 表示当前编号已被占用，进入下一轮。

成功创建的是空数据临时节点。它可以证明身份占用，却不能作为服务地址拨号。只有远端创建完成并且再次检查会话有效后，才把身份写入 `claimedProcess` 并返回。

创建期间发生其他异常时会释放整个代理。特别是取消 `.WaitAsync(stop)` 只是取消当前等待，不代表服务器一定撤销了已收到的创建请求；结果不确定时关闭会话，可以避免尝试另一个编号而遗留多个临时身份。

## 6. RegisterProcessAsync：把已经认领的身份发布为地址

```csharp
session.CheckActive();
if (claimedProcess != process)
{
    throw new InvalidOperationException("Only the identity claimed by this process can be published.");
}

ValidateAddress(process, address);
```

第一步确认会话有效，第二步要求传入身份与本代理认领的一致，避免替别人发布。地址先做本地检查，明显无效时不进行写入。

实际写入的是 UTF-8 字节：

```csharp
await Client.setDataAsync(ProcessPath(process), Encoding.UTF8.GetBytes(address))
    .WaitAsync(stop).ConfigureAwait(false);
session.CheckActive();
```

`setDataAsync` 修改已有节点，保留原来的临时身份。它不会再创建一个新身份。写完再次检查会话，只有整个过程正常结束后才记录 `ZkRegister` 日志。

写入失败或等待取消会关闭共享代理并传播异常；因为调用方不能仅凭本地等待失败就确定服务器没写入。

宿主在监听和首次依赖连接就绪之后才调用这里。连接方是否拥有身份，与该身份是否已经发布地址，是两项分别检查的事实。


## 7. WatchProcessesAsync：持续产生快照的异步迭代器

```csharp
public async IAsyncEnumerable<IReadOnlyList<DiscoveredProcess>> WatchProcessesAsync(
    [EnumeratorCancellation] CancellationToken stop)
```

返回类型有两层：每次得到一个 `IReadOnlyList<DiscoveredProcess>`，整个方法可以陆续产生多个这样的列表。`IAsyncEnumerable<T>` 表示异步序列，不是只返回一次结果的 `Task<T>`。

调用这个方法得到序列，并不意味着内部循环已经全部执行；枚举方通过 `await foreach` 或 `MoveNextAsync()` 推进它。`yield return` 交出一次结果，下一次枚举请求再从交出的位置继续。

`[EnumeratorCancellation]` 告诉编译器，这个参数用于异步枚举的取消，使枚举方传入的取消令牌能够参与迭代。代码中实际的网络等待和本地事件等待仍需要使用这个 token。

初始化父路径和事件观察者之后，循环读取父节点并同时注册观察：

```csharp
var children = await Client.getChildrenAsync(options.DiscoveryRoot, changed).WaitAsync(stop).ConfigureAwait(false);
var processes = new List<DiscoveredProcess>();
foreach (var child in children.Children)
{
    var process = await ReadDiscoveredProcessAsync(child, changed, stop).ConfigureAwait(false);
    if (process is not null)
    {
        processes.Add(process);
    }
}

session.CheckActive();
yield return processes;
await changed.WaitAsync(stop).ConfigureAwait(false);
```

`getChildrenAsync` 取得子节点名字，`ReadDiscoveredProcessAsync` 再逐个读取并过滤。返回列表是本轮收集到的有效成员快照，不是某一个新增或删除事件。

`yield return processes` 把快照交给调用方；消费方下一次推进枚举后，才执行后面的 `changed.WaitAsync`。如果读取或消费期间已经收到了通知，观察者会保留一个信号，因此这里可以立即继续下一轮。

每轮重新读取并注册观察，适应普通 ZooKeeper Watch 的一次触发语义。父节点观察处理成员增删，子节点数据观察处理地址发布和修改。它们不是完整的事件日志，代码依靠重新读取来收敛到当前状态。相关协议语义见 [ZooKeeper 官方 Watch 说明](https://zookeeper.apache.org/doc/current/zookeeperProgrammers.html#ch_zkWatches)。

因为父列表和各节点数据分开读取，不能把这一轮当成覆盖所有节点的原子事务快照。成员可能在读取过程中离开，方法需要容忍这种正常变化。

## 8. ReadDiscoveredProcessAsync：一条记录怎样变成可连接的成员

先检查名字：

```csharp
if (!child.StartsWith(NodePrefix, StringComparison.Ordinal))
{
    return null;
}

if (!ServerProcess.TryParse(child[NodePrefix.Length..], out var process))
```

`NodePrefix` 是 `server-`。前缀不匹配直接忽略。`child[NodePrefix.Length..]` 去掉前缀，得到例如 `Router1`，再交给统一的身份解析器。

`out var process` 表示解析方法通过输出参数交回结果。解析失败会记录非法节点日志并返回 `null`。返回类型 `Task<DiscoveredProcess?>` 中的 `?` 就表达“这条记录可能不应进入结果列表”。

日志使用的 `claimedProcess?.Role ?? nameof(ZooKeeperProxy)` 表示：已认领身份则使用它的 Role，否则用类名。`?.` 在对象为空时不继续访问，`??` 提供回退值，`nameof` 在编译时取得标识符名字。

下一步读取节点数据并注册数据观察：

```csharp
var data = await Client.getDataAsync(ProcessPath(process), changed).WaitAsync(stop).ConfigureAwait(false);
if (data.Data.Length == 0)
{
    return null;
}
```

读取成功但数据为空，表示身份尚未发布地址，因此不进入可拨号列表。数据观察在这一步已经注册，之后写入地址能触发重新读取，不需要等父节点再新增一个孩子。

对于非空地址：

```csharp
var address = ValidateAddress(process, Encoding.UTF8.GetString(data.Data));
if (data.Stat.getEphemeralOwner() == 0)
{
    throw new InvalidDataException($"Process {process.Name} must use an ephemeral node.");
}

return new DiscoveredProcess(process, address, data.Stat.getCzxid());
```

先把字节转回字符串并验证地址，再检查是否为临时节点。`getEphemeralOwner()` 为 0 表示它不具备这里要求的临时会话归属，不能当作活进程地址使用。

`getCzxid()` 是节点创建对应的事务标识，被用作发现记录的 `Generation`。名字和地址即使相同，节点删掉再创建后，代次也会变化。连接维护可以据此识别“同名的新实例”，而不是把它当作原连接一直存在。

这里没有通过 `Generation` 改写进程名，也没有增加实体路由；它只帮助发现层区分记录的生命周期。

## 9. 哪些发现错误被忽略，哪些继续传播

```csharp
catch (KeeperException.NoNodeException)
{
    // 快照读取期间成员离开，父节点 Watch 会触发下一次收敛。
    return null;
}
```

父列表刚列出一个孩子，读取它时可能已经被删除。这属于正常竞争，忽略这一项并等待后续快照即可。

```csharp
catch (InvalidDataException error)
{
    // 单条无效记录不能使已有健康连接停止；数据 Watch 保留以等待修正。
    log.Error(ServerLogEvents.ZkDiscover, claimedProcess?.Role ?? nameof(ZooKeeperProxy),
        peer: process.Name, detail: error.Message);
    return null;
}
```

非法地址或不符合要求的节点会记录错误并跳过，其他健康成员仍能被返回。以后数据修正时，可以通过已有观察重新读取。

这个方法没有捕获所有异常。会话失效、等待取消或其他客户端错误仍会传播给发现循环和监督方。不能把局部记录容错理解为整个发现任务会吞掉所有失败。

## 10. IsProcessClaimedAsync：有身份不等于已经可拨号

```csharp
session.CheckActive();
if (!ServerProcess.TryParse(name, out var process))
{
    return false;
}

var stat = await Client.existsAsync(ProcessPath(process)).WaitAsync(stop).ConfigureAwait(false);
session.CheckActive();
return stat is not null && stat.getEphemeralOwner() != 0;
```

方法先要求本地会话有效，再解析名字。非法实例名直接返回 `false`。合法名字则查询对应节点是否存在，并要求它具有临时会话归属。

这里没有读取地址，更没有要求地址非空，所以只占号未发布的进程也可以通过这一类身份检查。这正好配合启动流程：主动连接方可以在自己的地址尚未发布时，证明它持有有效进程身份。

相反，发现方法会过滤空地址。这两个方法关注的事实不同，不能用“能查到占号”代替“已经可拨号”。

## 11. ProcessPath 和 ValidateAddress：统一名字与连接边界

```csharp
private string ProcessPath(ServerProcess process) => $"{options.DiscoveryRoot}/{NodePrefix}{process.Name}";
```

这是表达式方法。`$"..."` 使用字符串插值，把发现根、固定前缀和统一格式的进程实例名拼起来。调用方不自行拼接一个不带编号的 `Router` 节点。

地址校验先选协议：

```csharp
var scheme = process.Type == ProcessType.Gate ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
```

这是简单条件表达式：Gate 使用 HTTPS，其余类型使用 HTTP。然后 `Uri.TryCreate` 尝试解析绝对 URI，并逐项检查：

- 主机必须是 `IPAddress.Loopback.ToString()`，当前为 `127.0.0.1`，不是任意公网主机或 `localhost` 字符串。
- 协议必须匹配当前进程类型。
- 不能包含用户名密码、查询字符串或片段。
- 解析后的绝对路径必须是 `/`，不能指向某个业务子路径。

任一条件失败就抛 `InvalidDataException`。多个 `||` 是短路判断，URI 解析失败时不会继续依赖一个有效的解析结果访问成员。

通过后返回 `uri.GetLeftPart(UriPartial.Authority)`，得到协议、主机及端口组成的规范化地址。发现记录使用这个返回值；发布前调用同一个校验器，确保写入满足同样的边界。

## 12. EnsurePathAsync 与 TryCreateEphemeralAsync：两种创建职责

创建父目录时：

```csharp
var path = "";
foreach (var segment in root[1..].Split('/'))
{
    session.CheckActive();
    path += "/" + segment;
    try
    {
        await Client.createAsync(path, [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT)
            .WaitAsync(stop).ConfigureAwait(false);
    }
    catch (KeeperException.NodeExistsException)
    {
    }
}
```

例如 `/tgame/test`，先尝试 `/tgame`，再尝试 `/tgame/test`。`root[1..]` 去掉开头斜杠，`Split('/')` 分段。空集合表达式 `[]` 在此按参数要求形成空数据，表示父目录不保存服务地址。

父目录使用 `PERSISTENT`，已经存在时就继续，不把“别人早已建好公共父路径”当作失败。只忽略 `NodeExistsException`，其他失败会传播。

`ZooDefs.Ids.OPEN_ACL_UNSAFE` 是当前调用使用的开放访问控制列表。它不构成调用方身份认证，不能把上述结构校验误读为已经建立了服务端访问权限限制。

`TryCreateEphemeralAsync` 的代码结构相似，但使用 `CreateMode.EPHEMERAL`，成功返回 `true`，遇到节点已存在返回 `false`。调用方据此尝试下一个进程或 worker 编号。

为什么不先 `exists` 再创建？两个进程可能同时看到不存在，真正的占用结果必须以创建操作的成功或冲突为准。这里直接尝试创建，让服务端协调同一路径的竞争。


## 13. AcquireWorkerAsync：为雪花 ID 申请一个 worker

这个内部方法供 ID 初始化调用，返回一个 `WorkerLease` 对象：

```csharp
internal async Task<WorkerLease> AcquireWorkerAsync(string root, CancellationToken stop)
```

`root` 来自 ID 配置的 worker 根路径。入口验证路径、检查 `Client` 和会话，并观察取消，然后创建与调用方令牌关联的申请期限：

```csharp
ZooKeeperOptions.ValidatePath(root);
_ = Client;
session.CheckActive();
stop.ThrowIfCancellationRequested();
using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
timeout.CancelAfter(options.ConnectTimeoutMilliseconds);
var token = timeout.Token;
```

`token` 同时受调用方取消和本地期限控制。下面创建目录、尝试编号和等待下一毫秒都用这个令牌，不是只给第一个网络操作计时。

确保父路径后，循环从 0 到 `SnowflakeSequence.MaxWorkerId`：

```csharp
for (int workerId = 0; workerId <= SnowflakeSequence.MaxWorkerId; workerId++)
{
    session.CheckActive();
    token.ThrowIfCancellationRequested();
    var workerPath = root + "/" + workerId.ToString(CultureInfo.InvariantCulture);
    if (!await TryCreateEphemeralAsync(workerPath, token).ConfigureAwait(false))
    {
        continue;
    }
```

雪花布局给 worker 分配 10 位，因此当前可用编号是 0～1023。编号转换使用 `InvariantCulture`，使路径中的数字文本不依赖进程当前文化设置。

`TryCreateEphemeralAsync` 返回假说明该路径已有占用，继续下一个编号。只有创建成功才进入后续流程；所有编号都被占用则抛 `InvalidOperationException`。

## 14. 为什么申请成功后还等下一毫秒

```csharp
long allocatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= allocatedAt)
{
    await Task.Delay(WorkerReuseWaitMilliseconds, token).ConfigureAwait(false);
}

token.ThrowIfCancellationRequested();
session.CheckActive();
return new WorkerLease(this, workerId, workerPath);
```

释放后，同一个 worker 编号可能被新生成器复用。新生成器的同毫秒序列从头开始，所以这里不在本地记录的申请毫秒内立即开始使用编号，而是等系统时间进入更晚的一毫秒。

`WorkerReuseWaitMilliseconds` 当前是 1。`Task.Delay` 是可取消的异步等待，不会像忙循环一样持续占用线程。等待结束后还要检查一次时间，所以一次 Delay 完成并不自动等于条件成立。

返回前再次检查取消和会话。只有这些检查都通过，才把实际编号、路径和所属代理封装成租约交给 ID 生成器。

这项等待降低同一编号在同毫秒重新起序列的风险，但它不是跨机器时钟同步机制，也不能单凭这段代码证明任意暂停、时钟偏差或网络时序下都绝无冲突。理解作用时应限定在代码实际检查的本地时间范围。

整个申请 `try` 内失败时会调用 `DisposeAsync()` 关闭共享会话。除了停止申请，也避免远端可能已创建、但本地没有拿到确认的 worker 节点被无人管理地遗留。

## 15. WorkerLease：把编号的释放责任交给对象

```csharp
internal sealed class WorkerLease(ZooKeeperProxy proxy, int workerId, string path) : IAsyncDisposable
{
    private ZooKeeperProxy? owner = proxy;
    internal int WorkerId { get; } = workerId;
    internal ZooKeeperSession Session { get; } = proxy.session;
```

类名后面的参数列表是主构造函数。创建 `new WorkerLease(this, workerId, workerPath)` 时，这些参数可以用于字段、属性初始化以及类内需要它们的方法。

`WorkerId` 告诉生成器使用哪个编号；`Session` 引用代理原有的会话，不会为每个 worker 新建连接。`owner` 保存负责真正删除节点的代理，允许在释放后变成 `null`。

`IAsyncDisposable` 表示清理可以包含异步操作。实际生成器释放时会等待这个租约的 `DisposeAsync()`：

```csharp
var previous = Interlocked.Exchange(ref owner, null);
if (previous is not null)
{
    await previous.ReleaseWorkerAsync(path).ConfigureAwait(false);
}
```

原子交换先取出旧 owner，同时清空字段。第一个释放者拿到代理并发起删除；后面的释放者看到旧值为空，不会重复删除同一路径。

这保证同一租约只发起一次这条清理路径，但不表示第二个并发释放调用会等待第一个调用的删除任务；这里没有保存并共享一个“释放完成任务”。因此“避免重复删除”和“所有并发释放调用都等到同一时刻”是不同的保证。

## 16. ReleaseWorkerAsync：会话有效时删除节点

```csharp
if (disposed || session.Lost.Task.IsCompleted)
{
    return;
}
```

代理已经关闭或会话已经失效时，不再尝试通过它删除。这个提前返回不表示远端节点在同一时刻已经消失；临时节点的后续清理由会话关闭或过期机制负责。

会话仍有效时：

```csharp
await Client.deleteAsync(path).WaitAsync(TimeSpan.FromMilliseconds(options.ConnectTimeoutMilliseconds)).ConfigureAwait(false);
```

删除节点是异步操作，这里使用带 `TimeSpan` 的 `WaitAsync` 限制等待时长。这个重载与前面的取消令牌重载不同：时间耗尽会以超时失败结束等待。

等待超时或删除异常会进入 `catch`，关闭代理后继续传播失败。它不悄悄假装租约已经成功释放，也不把删除失败后仍可任意复用身份作为正常状态。

## 17. DisposeAsync 和 CloseAsync 为什么分成两层

公开释放入口负责获取生命周期许可：

```csharp
await lifecycle.WaitAsync().ConfigureAwait(false);
try
{
    await CloseAsync().ConfigureAwait(false);
}
finally
{
    lifecycle.Release();
}
```

这里不接收业务取消令牌。即使运行任务已取消，清理仍需要等待正在进行的初始化释放许可，然后关闭会话。

内部关闭实际修改状态：

```csharp
if (disposed)
{
    return;
}

disposed = true;
session.Invalidate();
if (zk is not null)
{
    await zk.closeAsync().ConfigureAwait(false);
}
```

首先处理重复释放，再先将代理标记为已释放并通知会话失效，最后才等待网络客户端关闭。这样调用方不必等网络关闭完毕才得知对象不可继续使用。

`CloseAsync` 本身不再获取 `lifecycle`，所以既可以被已经持锁的初始化失败分支调用，也可以被公开释放入口调用。初始化失败并不需要重新进入同一信号量。

标记先于 `await` 设置，也意味着关闭异常之后对象仍处于已释放状态，后续调用不会自动重新建立连接。这条方法不是网络关闭一定成功的证明，关闭失败仍可能向调用方传播。

## 18. Changed：把很多 Watch 通知合并为一次重读

```csharp
private readonly Channel<byte> changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
{
    SingleReader = true,
    FullMode = BoundedChannelFullMode.DropWrite
});
```

`Channel<byte>` 是可异步读写的队列。容量为 1，存的 `byte` 没有业务信息，只表示“至少发生过一次变化，需要再读取”。`SingleReader = true` 表达这里只有一个枚举循环读信号，帮助队列按这个使用方式工作。

`DropWrite` 表示队列已满时丢弃新写入项。因为已有一个信号足以触发下一轮完整快照，重复通知无需无限排队。它保留的是需要重读的事实，不是每个事件的精确发生次数。

回调是：

```csharp
public override Task process(WatchedEvent evt)
{
    changes.Writer.TryWrite(0);
    return Task.CompletedTask;
}
```

回调不在内部读取所有成员，只尝试写入占位信号，然后立即返回已完成任务。这样网络事件处理和较慢的快照获取分开进行。

读取端是：

```csharp
internal ValueTask<byte> WaitAsync(CancellationToken stop) => changes.Reader.ReadAsync(stop);
```

若队列已有信号，读取可以立即完成；否则异步等待新信号，或由取消结束等待。返回的字节值不参与业务判断，外部只关心等待何时结束。

因此，读取期间发生变化、消费快照期间发生变化，都可以通过这个容量为 1 的队列保留“再读一次”的要求。它无法替代事务，也不承诺保留每个短暂成员状态。

## 19. 读完整个类后，把所有权关系连起来

```text
App 根容器
  → 一个 ZooKeeperProxy
      → 一个实际 ZooKeeper 客户端和一个 ZooKeeperSession
      → 本进程的临时身份节点
      → ID 生成器持有的 WorkerLease
```

代理借用共享日志和配置，拥有客户端会话。ID 生成器拥有自己的 worker 租约，释放时向代理归还编号。宿主先停止后台发现与连接任务，再释放容器，保证使用方停止后才清理服务，并让 ID 租约先于 ZooKeeper 会话释放。

普通对端连接断开不会自动调用这里的 `Invalidate`；本进程与 ZooKeeper 的会话状态是另一条路径。当前实现观察到后者失效后，通过共享任务同时影响身份检查、ID 发号和宿主退出。

具体场景可对照 [ZooKeeperProxyChecks.cs](../../tests/engine/ZooKeeperProxyChecks.cs.md)：初次握手未完成、编号竞争、空地址过滤、地址变更、同名节点重建和身份复用。ID worker 的集成检查还需结合 [IdGeneratorChecks.cs](../../tests/engine/IdGeneratorChecks.cs.md)，不能把进程发现测试当作所有 worker 场景都已覆盖。
