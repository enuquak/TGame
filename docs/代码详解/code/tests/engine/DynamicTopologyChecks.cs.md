# DynamicTopologyChecks.cs 代码详解

源码：[code/tests/engine/DynamicTopologyChecks.cs](../../../../../code/tests/engine/DynamicTopologyChecks.cs)

本文件在内存里模拟进程连接的加入、退出和恢复，检查首次启动完成后 Ready 是否保持为真。它直接使用真实 ServerNode 的连接登记和清理逻辑，用 FrameStream 代替网络流。

阅读时始终区分两个状态：FirstLinks 记住“首次按类型达到门槛”这个历史事件；links 则记录现在还连接着哪些实例。连接消失会影响 RPC，但不会把已经成功完成的 FirstLinks 改回未完成。

## 1. 主构造函数保存测试依赖

~~~csharp
internal sealed class DynamicTopologyChecks(TestServices services)
{
    public async Task RunAsync(CancellationToken stop)
~~~

类名后面的 (TestServices services) 是 C# 主构造函数参数。调用 new DynamicTopologyChecks(services) 时传入共享测试依赖，实例方法可以使用它。sealed 表示不能继承此类，internal 表示程序集内部可见。

RunAsync 返回 Task，表示整个场景的完成；async 允许 await，不自动启动线程。stop 是调用者提供的取消令牌，不是本方法自己创建的新期限。[测试入口](Program.cs.md) 初始化本地 ID 服务后，在 --dynamic-topology 路径调用它。

TestServices 提供本地 ID、日志、尚未连接的 ZooKeeperProxy 和 Startup。当前 Startup 把每种类型首次门槛设为 1。本文件没有运行 ZooKeeper 占号或发现，不要求真实外部服务。

## 2. 先用 Router 证明“没有主动依赖”即可完成门槛

~~~csharp
var configuration = TestNodes.LoadConfiguration();
var router = services.CreateNode(new ServerProcess(ProcessType.Router, 1), configuration);
Check(router.FirstLinks.IsCompletedSuccessfully && router.Ready,
    "Router without outgoing dependencies must become ready without incoming processes.");
~~~

configuration 是真实 YAML 加载结果。ServerProcess 表示 Router1，名称由 FormatName 统一生成。CreateNode 把此身份、配置和测试服务交给 ServerNode。

当前 Router 的主动目标列表为空。构造函数调用 TryCompleteFirstLinks，遍历不到任何需要等待的类型，就把 firstLinks 标记成功。因此 IsCompletedSuccessfully 和 Ready 都应为真。

ServerNode 的实际属性为：

~~~csharp
public Task FirstLinks => firstLinks.Task;
public string Role => process.Role;
public bool Ready => firstLinks.Task.IsCompletedSuccessfully;
~~~

=> 在这里是表达式属性，不是 lambda。每次读取 Ready 都从同一任务状态推导，没有额外保存一份可被断线代码错误改写的布尔值。

这个 Router 对象没有监听，也没有发布地址；断言只证明其首次连接要求不依赖入站进程。生产宿主的完整启动仍有其他步骤。

## 3. Base42 才是后续连接变化的被测对象

~~~csharp
var node = services.CreateNode(new ServerProcess(ProcessType.Base, 42), configuration);
var streams = new Dictionary<string, FrameStream>();
var tasks = new Dictionary<string, Task>();
var router1 = ServerProcess.FormatName(nameof(ProcessType.Router), 1);
var router2 = ServerProcess.FormatName(nameof(ProcessType.Router), 2);
var router9 = ServerProcess.FormatName(nameof(ProcessType.Router), 9);
var gate = ServerProcess.FormatName(nameof(ProcessType.Gate), 77);
using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
~~~

Base 主动连接 Router 和 DBMgr，二者都达到本用例的门槛 1 才能首次就绪。Gate 主动连接 Base，所以从 Base 视角 Gate 是入站来源。Scene 虽然也连接 Router，但不会因此自动成为 Base 的合法入站来源。

streams 用实例名索引各条内存流；tasks 用相同名字索引连接生命周期任务。Dictionary<string, Task> 的值不是连接结果，而是“这条连接什么时候结束”的可等待对象。

nameof 取得类型名，FormatName 再加规范编号。42、9、77 特意说明实例编号不是被初始数量限制的固定列表。

CreateLinkedTokenSource(stop) 创建关联取消源。外层 stop 取消会带动 lifetime，测试也可主动取消 lifetime 清理剩余连接；反向取消 lifetime 不会取消调用者的 stop。using var 只保证最后释放取消源，主动取消仍需显式调用 Cancel。

## 4. 先看 Open：为什么保存 Task 而不是立即等待

~~~csharp
void Open(string peer)
{
    var stream = new FrameStream();
    stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
    streams[peer] = stream;
    tasks[peer] = node.RunLinkAsync(peer, services.Ids.GenId(), stream, stream, lifetime.Token,
        requireReady: node.AcceptsIncoming(peer));
}
~~~

Open 定义在 RunAsync 内部，是局部函数。它捕获 node、services、字典和 lifetime；不必将这些对象反复作为参数传入。

FrameStream 同时实现 IAsyncStreamReader<Frame> 和 IAsyncStreamWriter<Frame>。因此 RunLinkAsync 的读流、写流两个参数可以传同一个对象，但内部 Input、Output 是两个不同通道：

~~~text
测试写入 Input → 节点读取对端帧
节点写入 Output → 测试读取本端发出的帧
~~~

TryWrite 把 Hello 预先排入无界输入通道。TestNodes.Hello(peer) 创建一个 Hello 字段等于 peer 的 Frame。这使 RunLinkAsync 开始时就能读到期望的对端身份。

services.Ids.GenId() 为本条链路生成 ID；调用者已初始化测试 ID 服务。字典赋值保存流和任务，方便后面按实例名关闭。

RunLinkAsync 的任务持续到收发循环结束。如果 Open 在这里 await 它，第一条连接不关闭就无法执行后面的“增加连接”和“关闭连接”，场景会卡住。因此这里启动并保存任务，等 Close 或 finally 才等待结束。

## 5. requireReady 如何区分模拟入站与主动目标

~~~csharp
requireReady: node.AcceptsIncoming(peer)
~~~

冒号写法是命名参数。AcceptsIncoming 先解析对端身份，再查配置是否存在“对端类型 → 本端类型”的主动连接关系。

对 Base 而言，Router、DBMgr 是本端主动目标，AcceptsIncoming 为假。测试给它们注入对端 Hello，让内部连接逻辑直接登记；真实主动流程此前发送本端 Hello 的步骤在本内存测试中没有完整重演。

Gate 是入站来源，所以 requireReady 为真。RunLinkAsync 必须先确认 Base 已就绪，并在读到 Gate 的 Hello 后回复自己的 Hello，随后登记连接。

注意：直接调用内部 RunLinkAsync 跳过了正式 ConnectAsync 拨号和 AcceptConnectAsync 中 ZooKeeper 身份占号检查。AcceptsIncoming 这里只判断类型方向，不证明对端真实存活或拥有临时节点。

## 6. 只有一台 Router 不能替代缺少的 DBMgr 类型

~~~csharp
Open(router1);
Check(!node.FirstLinks.IsCompleted && !node.Ready, "One Router must not replace the missing DBMgr type.");
Open(ServerProcess.FormatName(nameof(ProcessType.DBMgr), 1));
await node.FirstLinks.WaitAsync(stop);
Check(node.Ready, "Base waited for incoming Gate connections.");
~~~

Open(router1) 先加入 Router1。首次门槛逐目标类型计算，Router 达到 1，但 DBMgr 仍为 0，所以 FirstLinks 应未完成，Ready 应为假。不能把“总连接数有一条”当成满足所有依赖。

当前内存流已预填 Hello，读操作和默认写操作可以同步完成，所以 Open 调用能够先推进到连接登记再在后续读取处等待。这是本测试的受控条件，不是说任何 async 方法在返回 Task 前都保证完成网络握手。

加入 DBMgr1 后，两个目标类型都达标。await node.FirstLinks.WaitAsync(stop) 等待门槛成功，stop 允许结束等待；取消此等待不会重新创建或重置 FirstLinks 本身。

此时 Gate 还没接入，但 Base 应已 Ready。若实现把入站 Gate 也当启动依赖，这个等待就无法正常结束。

## 7. 入站方向由类型决定，实例编号不设配置上限

~~~csharp
Check(node.AcceptsIncoming(gate), "Incoming identities must not have a configured number ceiling.");
Check(!node.AcceptsIncoming(ServerProcess.FormatName(nameof(ProcessType.Scene), 1)), "Unrelated incoming types must be rejected.");
~~~

gate 是 Gate77。配置声明 Gate → Base，因此这个合法规范身份应被允许；不会因为初始只启动一台 Gate 就拒绝 77 号。

Scene1 与 Base 没有对应的主动入站关系，必须拒绝。这里两条断言只调用方向判断方法，没有真的接收 Scene1，也没有查 ZooKeeper。

如果第一条失败，可能是把数量计划错误地当作实例白名单；如果第二条失败，可能是把“都属于同一个服务器组”或“共同依赖 Router”错误地当作可互连关系。

## 8. 加入第二台 Router，再逐条移除

~~~csharp
Open(router2);
Check(node.Ready, "An extra Router revoked readiness.");
await Close(router1);
Check(node.Ready, "Losing a redundant Router revoked readiness.");
await Close(router2);
Check(node.Ready && node.FirstLinks.IsCompletedSuccessfully,
    "Losing the last Router must preserve completed startup readiness.");
~~~

Router2 是额外实例。首次门槛 1 不是最多只能连接 1 台，后续允许同时连接多个匹配实例。加入额外实例也不应让 Ready 退回假。

关闭 Router1 时还有 Router2；关闭 Router2 后 Router 类型当前连接数归零。两次之后 Ready 都应保持为真，最后还明确要求 FirstLinks 保持成功。

这里 DBMgr1 仍连接着。因此本段直接覆盖的是“某个主动依赖类型全部断开”，不是“所有依赖同时全部断开”。后一种情况在 [DiscoveryLinkChecks.cs](DiscoveryLinkChecks.cs.md) 的空快照和 [StartupReadinessChecks.cs](StartupReadinessChecks.cs.md) 中另有场景。

## 9. Close 为什么必须等连接任务，而不只标记通道结束

~~~csharp
async Task Close(string peer)
{
    streams[peer].Input.Writer.Complete();
    await tasks[peer].WaitAsync(stop);
}
~~~

Complete 关闭输入写入端；读取方读完已排队数据后看到流结束。它不是直接删除 node 内部的连接记录。

RunLinkAsync 收发循环退出后，自己的 finally 才从 sessions 和 links 移除对端，并调用 link.Close 结束该链路和待处理调用。等待 tasks[peer] 使后面的 Ready 断言发生在这条生命周期任务结束之后。

Close 使用 stop 限制等待。若只调用 Complete 就立即断言，测试可能在连接尚未清理时读到旧状态，从而错误通过。

streams 和 tasks 中的已结束条目没有立即删除，因为后面清理还可再次 await 已完成任务。重复等待任务不会重新执行连接。

## 10. 没有 Router 时仍可等待 Ready，再加入 Router9

~~~csharp
await node.WaitUntilReadyAsync(stop);
Open(router9);
Check(node.Ready, "A replacement Router revoked readiness.");
~~~

这次 WaitUntilReadyAsync 位于 Router1、Router2 都已关闭之后。正确实现仍等待那个已经成功完成的任务，因此可以正常返回，不要求重新凑当前 Router 数量。

Router9 模拟另一个后来出现的匹配实例。它加入后 Ready 继续为真。这里是手动 Open 新连接，不是通过 ZooKeeper 发现或重连重试循环恢复；“允许恢复连接”和“发现机制自动重试”是不同覆盖范围。

保持 Ready 也不表示 Router RPC 在空窗期仍能成功。CallPeerAsync 会查询当前 links，无对应实例时返回 Unavailable。本文件没有发起这个空窗期 RPC，所以该行为来自对 [ServerNode.cs](../../server/engine/ServerNode.cs.md) 的对照。

## 11. 已就绪节点接受 Gate 入站，再观察入站退出

~~~csharp
Open(gate);
await streams[gate].Output.Reader.ReadAsync(stop);
Check(node.Ready, "An incoming peer revoked readiness.");
await Close(gate);
Check(node.Ready, "Losing an incoming peer revoked readiness.");
~~~

Open(gate) 的 requireReady 为真。节点读取预填的 Gate77 Hello，确认自己已就绪，写出本端 Hello。测试从 Output 读到一帧，确认节点至少推进到了输出阶段。

这句 ReadAsync 没把帧保存下来，也没有检查 Hello 内容。因此本文件没有直接断言“回复的身份文本一定是 Base42”；依据实现可知道此处预期输出是 Hello，但不能把未写出的断言算成已经验证。

Gate 加入不改变已完成的首次门槛，Gate 退出同样不应撤销它。入站类型本来也不属于 Base 主动依赖的首次数量要求。

## 12. 为什么 FirstLinks 只会完成一次

被测实现的关键分支是：

~~~csharp
if (Ready)
{
    return;
}
~~~

这是 TryCompleteFirstLinks 的开头。第一次尚未 Ready 时，它逐类型比较已握手实例数与 startup.GetStartupMinimum；所有类型满足后 firstLinks.TrySetResult 成功完成任务。

一旦 Ready 为真，后续登记连接再次调用这个方法会立即返回。断线的 finally 只删除当前连接和关闭 PeerLink，没有另建 FirstLinks，也没有撤销任务成功状态。

所以本场景有两条并行理解线：

~~~text
历史启动状态：未完成 → Router 与 DBMgr 达标 → 永久保留成功
当前链路集合：加入 Router1 → 加入 DBMgr1 → 增减 Router → 增减 Gate
~~~

“永久”是指这个 ServerNode 实例的既有成功状态，不表示进程永远不退出、会话永远有效或 RPC 永远成功。

## 13. finally 的取消与逐个等待

~~~csharp
finally
{
    lifetime.Cancel();
    foreach (var task in tasks.Values)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }
}
~~~

即使断言中途失败，也会进入 finally。先 Cancel 通知仍在读流的连接停止，再逐个 await，避免方法返回时留下仍使用共享 ID 或日志的后台任务。

catch 后面的 when 是异常过滤器：只在异常属于 OperationCanceledException 且 lifetime 已请求取消时忽略它。非取消异常不会被当成正常收尾。如果某个任务出现其他错误，异常会继续传播，源码没有保证此后所有循环项仍被逐一等待；不能把这个循环描述成吞掉一切错误的绝对清理保证。

TestServices 由调用方拥有，本测试不释放它。它只负责自己创建的关联取消源和连接任务。ServerNode 本身不是在此处由容器创建的需要单独异步释放的服务；结束连接是这里的核心清理动作。

## 14. 如何阅读成功信息和测试边界

Check 的实现只在条件为假时抛 Exception(message)。本文件的 PASS 输出位于 try 末尾、finally 之前，因此出现输出后仍要完成连接收尾，RunAsync 正常返回才算整组结束。

本文件覆盖本地连接登记的类型门槛、额外实例、某类型全断开后的稳定 Ready、高编号入站、后续新连接。它不覆盖真实发现、网络地址、ZooKeeper 临时身份、重试延迟或完整 RPC 行为。真实传输与快照替换继续看 [DiscoveryLinkChecks.cs](DiscoveryLinkChecks.cs.md)，发布与入站身份边界看 [StartupPublicationChecks.cs](StartupPublicationChecks.cs.md)。本文没有执行这些测试。
