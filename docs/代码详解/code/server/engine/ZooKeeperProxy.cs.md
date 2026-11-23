# ZooKeeperProxy.cs 代码详解

源码：[code/server/engine/ZooKeeperProxy.cs](../../../../../code/server/engine/ZooKeeperProxy.cs)

## 三类 ZooKeeper 节点

每个进程从 App 的容器取得一个 `ZooKeeperProxy`。它的客户端会话同时管理进程身份节点和 ID worker。其他进程通过同一发现根读取服务地址。构造函数检查配置，保存共享的 `GameLog`，网络连接由 App 显式初始化。见 [Program.cs](../app/Program.cs.md)、[ServerServices.cs](../app/ServerServices.cs.md)、[ZooKeeperOptions.cs](ZooKeeperOptions.cs.md)。

| 例子 | 创建模式 | 节点内容和用途 |
| --- | --- | --- |
| `/tgame/test`、`/tgame/id-workers` | 持久节点 `PERSISTENT` | 父目录，容纳多次运行的临时节点 |
| `/tgame/test/server-Router1` | 临时节点 `EPHEMERAL` | 起初数据为空，表示 Router1 被认领。发布后数据是服务地址的 UTF-8 字节 |
| `/tgame/id-workers/0` | 临时节点 `EPHEMERAL` | 数据为空，路径名本身表示同一个 worker 编号已有人使用 |

临时节点随其所属 ZooKeeper 会话关闭或过期被清理。进程失去网络连接和服务端删除临时节点可能发生在不同时间。应用侧对已连接会话的断开会立即标记失效，旧进程不能继续拿它发号。见 [ZooKeeperSession.cs](ZooKeeperSession.cs.md)。

## 字段与访问入口

- `zk` 是实际客户端引用，创建时就赋值。`initialized` 是握手成功且检查通过后的独立标记，使用 `volatile` 发布。`Client` 先拒绝已释放、未初始化的调用，最后返回 `zk!`。这里的后缀 `!` 只向编译器表明此处已能保证非空。
- `lifecycle = new(1)` 序列化初始化与释放，供异步流程跨 `await` 持有许可。普通 ZooKeeper 查询不普遍经过这个信号量，会话活性仍要单独检查。
- `session` 持有握手和失效信号。`SessionLost` 先用 `_ = Client` 触发访问检查，再返回 `session.Lost.Task`。下划线表示舍弃属性的返回值，属性里的检查仍然会运行。
- `claimedProcess` 保存成功占到的完整身份。`Process` 先要求会话处于活动状态，再返回这个字段。`claiming` 是用来阻止一个代理多次进入认领的整数标记。

## InitializeAsync 等待有效会话

1. 用调用方的 `stop` 等待生命周期许可。取得许可后拒绝已释放的实例，也拒绝重复初始化。
2. `CreateLinkedTokenSource(stop)` 创建一个能随调用方取消的令牌源，`CancelAfter(options.ConnectTimeoutMilliseconds)` 再加上本地截止时间。`using var` 会在作用域退出时释放令牌源。
3. 构造 `ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, session)`，再等待 `session.Connected.Task.WaitAsync(timeout.Token)`。如果鉴权失败也解除了等待，后面的 `session.CheckActive()` 会报出会话不可用。取消检查同样排在 `initialized = true` 之前。
4. 创建或等待阶段抛错时调用 `CloseAsync()`，关闭后用 `throw;` 向外传播。无论结果如何，最外层的 `finally` 都归还许可。仅仅在等许可时被取消会在进入 `try` 前退出。

例如客户端已经分配但尚未完成首次握手时，`zk` 非空，而外部读取 `SessionLost` 仍会因 `initialized` 为假被拒绝。待初始化方法成功结束才可以访问这个监督任务。

```csharp
await session.Connected.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
timeout.Token.ThrowIfCancellationRequested();
session.CheckActive();
initialized = true;
```

## ClaimProcessAsync 从 1 开始尝试

认领先校验 `ProcessType` 枚举值并通过 `Client` 和 `CheckActive` 检查。`Interlocked.Exchange(ref claiming, 1)` 原子地写入 `1` 并返回旧值。只有旧值为 `0` 的调用可以继续。过早调用或重复调用在操作块之外直接失败，保护首次有效操作及其会话。

`EnsurePathAsync` 去掉路径开头的 `/` 并切分，按层创建父节点。例如 `/tgame/test` 会依次尝试 `/tgame`、`/tgame/test`。使用 `[]` 传入空数据。并发进程可能已经建好同一路径，所以只把 `NodeExistsException` 当作正常结果。节点使用库的 `OPEN_ACL_UNSAFE`。

随后构造 `new ServerProcess(type, number)` 并尝试创建空数据的临时节点。`TryCreateEphemeralAsync` 在收到 `NodeExistsException` 时返回假，其余失败交给调用方处理。若 `server-Router1` 已存在，就用 `checked(number + 1)` 检查溢出并进入 Router2。成功后再次检查会话活性，然后保存并返回该身份。

`WaitAsync(stop)` 可以取消当前等待。取消时 ZooKeeper 端可能已经执行创建，因此进入实际认领流程后出错会调用 `DisposeAsync` 关闭会话，避免继续尝试其他编号而保留一次结果不明的认领。

## RegisterProcessAsync 在原占号节点上发布

`RegisterProcessAsync` 检查会话以及传入身份是否等于 `claimedProcess`。接着调用 `ValidateAddress`，要求可解析为绝对 URI、解析后的主机为 `127.0.0.1`，且 `Gate` 用 HTTPS、其他类型用 HTTP。URI 不能含用户信息、非根路径、查询参数或片段。读取发现数据时使用该方法返回的 authority，例如将 `http://127.0.0.1:12345/` 规范为 `http://127.0.0.1:12345`；发布则使用传入字符串的原始字节。

前置检查通过后，用 `setDataAsync` 更新同一个临时节点，再确认会话仍活动，最后记录 `ZK_REGISTER` 日志。前置检查失败会直接拒绝调用，写入等待或后续会话检查失败则关闭代理。App 在 ID 初始化、监听和首次连接门槛达成后调用发布方法，随后记录 `PROCESS_READY`。首次完成后 `Ready` 保持为真，运行期间连接断开由连接维护处理。

## WatchProcessesAsync 如何重读快照

`IAsyncEnumerable<IReadOnlyList<DiscoveredProcess>>` 是异步序列。消费方每次等待 `MoveNextAsync()`，拿到一个完整进程列表；执行到 `yield return processes` 时，生成端暂停，直到消费方要求下一项。`[EnumeratorCancellation]` 将异步枚举器的取消令牌传给循环里的 `stop`。见 [ServerNode.Discovery.cs](ServerNode.Discovery.cs.md)。

1. 确保发现根存在，建立一个 `Changed` watcher。每轮先检查会话和取消，再用 `getChildrenAsync(root, changed)` 读取子项，同时订阅父节点的成员增删。
2. 逐个处理子项，只解析带 `server-` 前缀且能转成 `ServerProcess` 的名称。`child[NodePrefix.Length..]` 从前缀尾部切到字符串末尾；`out var process` 接收解析结果。非法名称记录错误后跳过。
3. 用 `getDataAsync(path, changed)` 读取节点数据并订阅数据变化。空数据表示只占号，先跳过。非空数据必须是合法服务地址，并且 `getEphemeralOwner()` 非零。合法记录携带进程身份、规范化地址和创建事务 ID `czxid`，最后一项作为 `Generation`。
4. 单个节点在读取期间被删，捕获 `NoNodeException` 并跳过；地址或节点类型不合法，捕获 `InvalidDataException`、记日志并跳过。其他错误传给枚举消费者。产出列表前再次检查会话。
5. 产出完整列表后，下一次推进时等待 `changed.WaitAsync(stop)`。ZooKeeper 的 watch 用于提示变更，每轮读取后重新注册。`Changed.process` 把字节 `0` 调用 `TryWrite` 塞进容量为 `1` 的 channel，已有未消费提示时可丢弃新增提示。读取方只需知道应重新取快照，因此多个事件可以合并。

例如 Router2 先创建空节点，父 watch 提醒读取，此时快照仍只有 Router1。Router2 随后写入地址，子节点的数据 watch 会唤醒下一次扫描。即使某个 `Scene1` 节点删后马上建回同名同地址，创建事务 ID 也会变化，连接维护可据此换掉旧连接。见 [DiscoveredProcess.cs](DiscoveredProcess.cs.md)。

## IsProcessClaimedAsync 供入站连接检查

查询前后都检查会话是否活动。先用 `ServerProcess.TryParse` 检查名称，合法后用 `existsAsync` 读节点元信息。`stat is not null && stat.getEphemeralOwner() != 0` 使用短路与，同时要求节点存在且是临时节点。对这里的普通持久节点和临时节点，非零值是节点所属的 ZooKeeper 会话 ID。

查询只查名称是否被临时占用，空数据同样满足，可以让刚占号的进程主动向依赖发起握手。`ServerNode.AcceptConnectAsync` 另外检查拓扑、接收端就绪及握手身份。此查询本身没有验证远端会话凭据。见 [ServerNode.cs](ServerNode.cs.md)。

## AcquireWorkerAsync 与租约释放

`AcquireWorkerAsync` 先校验 worker 根路径、客户端和会话，检查取消，再为整个申请流程设置与调用方令牌关联的本地截止时间。建好父路径后，从 `0` 到 `SnowflakeSequence.MaxWorkerId` 依次尝试创建空临时节点。竞争者抢先创建时继续试下一号。全部 `1024` 个都被占用会抛 `InvalidOperationException`。

```csharp
long allocatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= allocatedAt)
{
    await Task.Delay(WorkerReuseWaitMilliseconds, token).ConfigureAwait(false);
}
```

创建成功后记下当前 UTC 毫秒，每次用 `Task.Delay(1, token)` 等待，直到再次读到的毫秒严格大于记录值。比如在毫秒 `100` 收到创建成功，最快在 `101` 以后交付 worker。这让刚被复用的编号避开申请完成所在的同一毫秒，降低与上一个拥有者重复发号的风险。等待同时受申请时限约束，最后还会检查取消和会话状态。申请过程失败和占号类似，会关闭共享会话。见 [SnowflakeSequence.cs](identifiers/SnowflakeSequence.cs.md)、[SnowflakeIdGenerator.cs](identifiers/SnowflakeIdGenerator.cs.md)。

内部 `WorkerLease(ZooKeeperProxy proxy, int workerId, string path)` 使用 C# 主构造函数语法。`WorkerId` 和 `Session` 属性从参数初始化；`path` 在释放时用于定位临时节点。`Interlocked.Exchange(ref owner, null)` 由一个释放调用取得原代理引用，余下调用看到 `null` 即结束。代理和会话仍有效时，`ReleaseWorkerAsync` 在操作时限内删除该节点。若删除失败，关闭共享会话后继续抛错。代理已关闭或会话已失效时，跳过显式删除。

## 关闭顺序

`DisposeAsync` 取得生命周期许可，调用 `CloseAsync`。后者先检查 `disposed`；首次关闭时先设置该标记、调用 `session.Invalidate()` 唤醒监督者，再异步关闭已创建的客户端。它使用注入的日志记录事件，日志由 App 最后释放。宿主停止后台任务和监听后释放容器，ID 服务先归还租约，再关闭代理。关闭的代理需由重新启动的进程重新创建。见 [IdGenerator.cs](identifiers/IdGenerator.cs.md)、[ZooKeeperProxyChecks.cs](../../tests/engine/ZooKeeperProxyChecks.cs.md)、[IdGeneratorChecks.cs](../../tests/engine/IdGeneratorChecks.cs.md)。
