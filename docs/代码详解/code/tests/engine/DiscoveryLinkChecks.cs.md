# DiscoveryLinkChecks.cs 代码详解

源码：[code/tests/engine/DiscoveryLinkChecks.cs](../../../../../code/tests/engine/DiscoveryLinkChecks.cs)

这份测试把真实 HTTP/2 gRPC 连接与可控的内存发现列表组合起来：启动三台本机模拟 Router，让真实 ServerNode 处理发现快照、握手、连接增减、同名替换和发现故障。

它没有连接真实 ZooKeeper。这里模拟的是“发现层输出哪些记录”，网络拨号和基础 RPC 则实际运行。与 [DynamicTopologyChecks.cs](DynamicTopologyChecks.cs.md) 直接喂内存流相比，本文件多验证了一层真实传输和后台连接维护。

## 1. 文件中的两个类分别扮演谁

~~~csharp
internal sealed class DiscoveryLinkChecks(TestServices services)
~~~

DiscoveryLinkChecks 是测试导演。主构造函数接收共享 TestServices，RunAsync 按顺序发布发现列表、等待事件并检查状态。internal 表示程序集内可见，sealed 表示不可继承。

~~~csharp
internal sealed class DiscoveryPeer(string name, IdGenerator ids) : ServerLink.ServerLinkBase
~~~

DiscoveryPeer 是模拟远端。它继承生成的 gRPC 服务基类，重写 Connect 方法，提供特意设计的握手和应答观察点。它没有使用正式 LinkService，所以不会检查 ZooKeeper 占号；不能把它的接入行为当成生产入站校验。

测试中的真实节点是 Scene1，三个模拟远端分别是 Router1、Router2、Router3。

## 2. 先创建服务对象，再分别启动本机应用

~~~csharp
var configuration = TestNodes.LoadConfiguration();
var first = new DiscoveryPeer(ServerProcess.FormatName(nameof(ProcessType.Router), 1), services.Ids);
var second = new DiscoveryPeer(ServerProcess.FormatName(nameof(ProcessType.Router), 2), services.Ids);
var third = new DiscoveryPeer(ServerProcess.FormatName(nameof(ProcessType.Router), 3), services.Ids);
await using var firstApp = await StartAsync(first, stop);
await using var secondApp = await StartAsync(second, stop);
await using var thirdApp = await StartAsync(third, stop);
~~~

每个 DiscoveryPeer 保存自己的名字，共享调用方已初始化的本地 ID 服务。FormatName 保证类型名与编号的拼接统一。

await using var firstApp = await StartAsync(...) 有两个不同作用：右边 await 等待应用启动，左边 await using 安排作用域退出时异步释放应用。第二台在第一台启动成功后才开始启动，源码没有用 WhenAll 并行启动三台。

这三个应用是同一测试进程中的独立监听端点，不是三个操作系统进程。

## 3. StartAsync 怎样得到一个实际可连接的地址

~~~csharp
private static async Task<WebApplication> StartAsync(DiscoveryPeer peer, CancellationToken stop)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
        listen => listen.Protocols = HttpProtocols.Http2));
    builder.Services.AddGrpc();
    builder.Services.AddSingleton(peer);
    var app = builder.Build();
    app.MapGrpcService<DiscoveryPeer>();
    await app.StartAsync(stop);
    return app;
}
~~~

CreateSlimBuilder 创建较精简的 ASP.NET Core 应用构建器。ClearProviders 去掉默认宿主日志提供者，避免这些测试端点输出无关日志；这与 TestServices 中的 GameLog 是不同系统。

ConfigureKestrel 接收 lambda。外层 options 是服务器配置对象，Listen 在回环地址监听端口 0；0 表示让操作系统选择空闲端口。内层 listen 设置只使用 HTTP/2，满足这里的 gRPC 流式连接要求。

AddGrpc 注册 gRPC 基础服务，AddSingleton(peer) 注册已经创建的模拟对象。MapGrpcService<DiscoveryPeer>() 将生成的服务路由映射到此类型。

StartAsync(stop) 真正启动监听；等待成功后才返回应用。调用者随后从 Urls.Single() 取得唯一实际地址。Single 不只是拿第一项，它还要求恰好一项，否则会抛异常。

若应用在返回给调用者前启动失败，调用者的 await using 还未取得该对象；本辅助方法没有显式 catch 清理这一失败路径。本文描述正常路径的所有权，不假设源码有额外未写出的失败补偿。

## 4. 发现记录有身份、地址和代次

~~~csharp
var router1 = new DiscoveredProcess(new ServerProcess(ProcessType.Router, 1), firstApp.Urls.Single(), 1);
var router2 = new DiscoveredProcess(new ServerProcess(ProcessType.Router, 2), secondApp.Urls.Single(), 2);
var router3 = new DiscoveredProcess(new ServerProcess(ProcessType.Router, 3), thirdApp.Urls.Single(), 3);
var changes = Channel.CreateUnbounded<IReadOnlyList<DiscoveredProcess>>();
~~~

DiscoveredProcess 是 record，包含 Process、Address、Generation。record 默认按成员值比较，适合判断“这个名字对应的发现记录是否仍是同一代、同一地址”。

这里代次 1、2、3 是测试提供的值，不是真实 ZooKeeper 事务号。生产发现用节点创建事务号 Czxid 区分同名节点重建；本测试稍后会人工改 Generation 模拟这种变化。

changes 是无界通道，元素类型是 IReadOnlyList<DiscoveredProcess>。每个元素是一份完整发现快照，不是增量命令：

~~~text
[Router1, Router2] → 当前应维护这两个目标
[Router2]          → Router1 已离开，需要停止对应任务
[]                 → 当前没有任何目标
~~~

通道的 Writer 由测试写，Reader 由节点消费。IReadOnlyList 限制消费者通过该接口修改列表，本测试发布后也不会去修改已交出的列表内容。

## 5. 把 Router 首次门槛设为 2

~~~csharp
var counts = System.Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
counts[ProcessType.Router] = 2;
var node = new ServerNode(new ServerProcess(ProcessType.Scene, 1), configuration, new ServerGroup(counts),
    services.ZooKeeper, services.Ids, services.Log);
using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
var maintenance = node.MaintainConnectionsAsync(changes.Reader.ReadAllAsync(lifetime.Token), lifetime.Token);
~~~

Enum.GetValues 获取所有进程类型，ToDictionary 给每种类型初始值 1；_ 表示这个值函数不使用输入参数。再单独把 Router 改为 2，构造本场景的 ServerGroup。

Scene 的主动目标只有 Router，因此需要两个不同 Router 实例握手登记，才能首次完成启动。其他类型在 counts 中有值，并不表示 Scene 必须连接所有类型。

生产数量来自配置的 processes，这里手动构造 ServerGroup 是为了精确制造门槛 2 的测试条件，没有新增生产数量来源。

ReadAllAsync 把 ChannelReader 变成 IAsyncEnumerable<IReadOnlyList<DiscoveredProcess>>：每来一份列表，异步枚举就提供下一项。内部 MaintainConnectionsAsync 重载接收这个序列，代替正常调用的 ZooKeeper Watch。

maintenance 保存持续运行的任务。此时不能立即 await 它结束，因为后面还要提供多个快照；它只会在取消、发现结束或错误时结束。

## 6. 连接维护怎样从快照变成具体任务

对应 [ServerNode.Discovery.cs](../../server/engine/ServerNode.Discovery.cs.md)，维护层先筛出 Scene 应主动连接的类型，再按实例名建立目标字典。它比较新快照与当前 workers：

~~~csharp
if (targets.TryGetValue(name, out var current) && current == worker.Peer)
{
    continue;
}
~~~

名字存在且整个 record 相等，就保留任务。名字消失、地址变化或 Generation 变化，都会取消旧 worker 并等待它退出；随后为新增目标创建新的维护任务。

每个 worker 里面调用 MaintainLinkAsync，负责实际 ConnectAsync 及允许的断线重试。因此“发现快照不变时连接重试”与“发现记录变化时换一个 worker”是两个层次。本文件主要按快照驱动后者。

## 7. Connection 的两个信号不是同一个事件

~~~csharp
internal sealed class Connection
{
    internal TaskCompletionSource Handshaken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal Channel<Connection> Connections { get; } = Channel.CreateUnbounded<Connection>();
~~~

每次 gRPC Connect 调用会创建一个 Connection。TaskCompletionSource 是可由代码主动完成的任务源；等待者使用它的 Task，触发者调用 TrySetResult。

Handshaken 表示本模拟服务已收到符合条件的基础 RPC 回复。Closed 表示这次服务调用进入 finally 并发出结束信号。它们不是布尔标记，而是可以被 await 的一次性通知；先完成再等待也不会丢事件。

RunContinuationsAsynchronously 避免设置结果时直接在当前调用栈内执行等待者的后续代码，不是给每个信号创建专用线程。new(...) 由属性类型推断构造类型，get; 表示调用者不能把整个属性替换成别的任务源，但仍能使用该对象的方法。

Connections 通道向测试交出每次新调用的 Connection。得到对象只表示服务已推进到发布通知的位置，不能据此当作 Handshaken 已完成。

## 8. DiscoveryPeer.Connect 先验证调用方 Hello

~~~csharp
var connection = new Connection();
try
{
    if (!await input.MoveNext(context.CancellationToken) || input.Current.Hello != ServerProcess.FormatName(nameof(ProcessType.Scene), 1))
    {
        throw new InvalidDataException("Unexpected caller identity.");
    }

    await output.WriteAsync(new Frame { Hello = name }, context.CancellationToken);
~~~

Connect 的 input 是来自 Scene 的请求流，output 是发回 Scene 的响应流。MoveNext 等待下一帧，返回 false 表示流已结束。|| 短路保证没有帧时不继续检查 Current.Hello。

首帧必须是 Scene1 的 Hello。成功后，模拟 Router 写回自己的 name。双方的 Hello 交换身份，不携带业务实体或路由目标。

context.CancellationToken 来自当前 gRPC 调用。节点取消某个 worker 时，远端调用可通过这个令牌停止读写。

## 9. 为什么 Handshaken 要等一次 Unimplemented 回复

~~~csharp
var requestId = ids.GenId();
await output.WriteAsync(RpcFrames.Request(new Empty(), requestId), context.CancellationToken);
Connections.Writer.TryWrite(connection);
await foreach (var frame in input.ReadAllAsync(context.CancellationToken))
{
    if (frame.Reply?.RequestId != requestId ||
        frame.Reply.Error?.StatusCode != (int)StatusCode.Unimplemented)
    {
        throw new InvalidDataException("Expected an unimplemented reply after the process handshake.");
    }

    connection.Handshaken.TrySetResult();
}
~~~

模拟 Router 在 Hello 后立即向 Scene 发一条 Empty 请求，并为它生成 requestId。Empty 是 protobuf 的空消息类型，这里只需要一个合法负载来测试基础收发，不执行游戏业务。

接着把 connection 放进通知通道，再用 await foreach 持续等待 Scene 发来的帧。异步枚举在没有数据时等待，而不是忙循环。

frame.Reply?.RequestId 使用空条件访问；没有 Reply 时得到 null，与期望 ID 不相等，第一项为真，|| 短路避免后续继续当成合法回复处理。第二项要求错误状态码是 Unimplemented；(int) 把枚举转换为协议字段存储的整数。

Scene 当前未接入业务处理，ServeAsync 正确行为是回复 Unimplemented，而不是伪造成功。只有回复关联到这次 requestId 且错误码正确，才完成 Handshaken。

因此这个名字为 Handshaken 的信号，证据强于“TCP 已连接”或“收到 Hello”：它还证明当前链路能完成一次关联的基础请求与回复。首次门槛是在节点登记链接时计数，不需要等待这个测试信号；信号只是测试额外选择的观察点。

即便 Scene 只有一个 Router、尚未 Ready，已建立的主动连接仍能处理这种基础 RPC。Ready 的入站门槛不等于禁止主动链路在首次达标前收发协议。

## 10. 只有 Router1：确认通信已通后，仍不能就绪

~~~csharp
changes.Writer.TryWrite([router1]);
var old = await first.Connections.Reader.ReadAsync(stop);
await old.Handshaken.Task.WaitAsync(stop);
if (node.FirstLinks.IsCompleted || node.Ready)
{
    throw new Exception("Discovery completed startup before two Routers were connected.");
}
~~~

[router1] 是集合表达式，在此转换成通道需要的列表。写入后，维护循环发现目标、真实拨号，模拟服务将 Connection 放进 first.Connections。

ReadAsync 等到该对象，再等待 Handshaken。这避免一种虚假的通过方式：第一条连接根本没建成，所以节点暂未 Ready。到断言位置，已经有通信证据，但只连接一台，仍应未达 2 台门槛。

检查 IsCompleted 比只检查 IsCompletedSuccessfully 更强：此时 FirstLinks 应尚未结束，不能意外以失败或取消状态结束。Ready 同时应为假。

## 11. 加入 Router2 达标，再加入 Router3 证明没有连接上限

~~~csharp
changes.Writer.TryWrite([router1, router2]);
var extra = await second.Connections.Reader.ReadAsync(stop);
await extra.Handshaken.Task.WaitAsync(stop);
await node.FirstLinks.WaitAsync(stop);
changes.Writer.TryWrite([router1, router2, router3]);
var late = await third.Connections.Reader.ReadAsync(stop);
await late.Handshaken.Task.WaitAsync(stop);
if (!node.Ready)
{
    throw new Exception("Adding an extra Router revoked readiness.");
}
~~~

第二份列表仍含 Router1，因此第一条 worker 应被保留，只增加 Router2。等第二台完成基础通信后，再显式等待 FirstLinks，确保首次启动成功。

第三份列表加入 Router3。读取 third.Connections 并等待 Handshaken，证明维护循环确实连接了它，而不是只把记录存起来。Router 首次门槛为 2，不代表连接达到 2 后停止发现或拒绝额外实例。

额外连接到来后 Ready 仍应为真。第一次成功不能被后续列表重置成另一个等待任务。

## 12. 快照只剩 Router2：跌破首次门槛也不撤销成功

~~~csharp
changes.Writer.TryWrite([router2]);
await old.Closed.Task.WaitAsync(stop);
await late.Closed.Task.WaitAsync(stop);
if (!node.Ready)
{
    throw new Exception("Removing a redundant Router revoked readiness.");
}
~~~

完整列表只有 Router2，所以 Router1 和 Router3 的 worker 都应被取消。等待 old.Closed、late.Closed，观察两台模拟远端的调用已经进入结束清理。

Closed 是远端 finally 的信号，不是独立证明本地所有清理语句都已执行完的屏障。维护层自身还会 await worker.Task 后移除 worker。本段至少避免了“只写快照、还未观察任何断开就立即断言”。

此时只剩一台 Router，小于首次门槛 2，但 Ready 应保持 true。FirstLinks 记录曾经达标，不是实时健康计数。

## 13. 同名同地址只改变 Generation，也要重新连接

~~~csharp
changes.Writer.TryWrite([router2 with { Generation = 3 }]);
await extra.Closed.Task.WaitAsync(stop);
var replacement = await second.Connections.Reader.ReadAsync(stop);
await replacement.Handshaken.Task.WaitAsync(stop);
~~~

with 创建新的 DiscoveredProcess，保留身份和地址，只修改 Generation。它不会改变原 router2 变量指向的记录。

维护层比较整条记录，发现它不同于原 worker.Peer，便停止旧调用，再开始新调用。测试先等旧 extra 关闭，再从同一个 second.Connections 通道拿到新的 replacement 并完成通信。

这里没有重启 secondApp，也没有改变端口。场景刻意隔离“同名节点重建导致代次变化”这一信号，证明只比较名称和地址还不够。Generation 与 router3 同为 3 不冲突；代次比较针对同一个实例名，不是要求所有记录的代次全局互异。

## 14. 全部目标离开，再只恢复一台

~~~csharp
changes.Writer.TryWrite([]);
await replacement.Closed.Task.WaitAsync(stop);
if (!node.Ready)
{
    throw new Exception("Removing all discovered peers revoked completed startup readiness.");
}

changes.Writer.TryWrite([router1]);
var restored = await first.Connections.Reader.ReadAsync(stop);
await restored.Handshaken.Task.WaitAsync(stop);
if (!node.Ready)
{
    throw new Exception("Reconnecting a Router revoked readiness.");
}
~~~

[] 表示空快照。此时唯一保留的 replacement 应关闭，Scene 没有任何主动目标连接，但已经完成的启动仍保持 Ready。

随后只公布 Router1。它先前的 old 调用已经结束，现在应产生新的 restored 调用。等待 Handshaken 证明发现维护在全断开后仍然活着，并且真实基础通信恢复。

恢复时没有 Router2，仍低于最初门槛 2。正确实现不会重新要求两台才能继续保留 Ready。这里不声明空窗期 RPC 可用；没有连接时调用仍应 Unavailable。

## 15. 发现流报错，维护任务必须向调用者传播

~~~csharp
changes.Writer.TryComplete(new InvalidDataException("Discovery failed."));
try
{
    await maintenance;
    throw new Exception("Discovery failure was hidden.");
}
catch (InvalidDataException)
{
}
~~~

TryComplete(error) 使发现通道以错误结束。它不是空快照，也不是正常取消；ReadAllAsync 后续会传播该失败。

await maintenance 观察整个维护任务。如果它正常返回，后面的 throw 主动报告“发现故障被隐藏”。只有 InvalidDataException 被视为预期，其他异常继续暴露。catch 也接受派生类型，并没有比较异常对象身份或消息。

被测维护方法在 finally 取消内部 lifetime，等待各 worker 清理，再让失败向外传播。这样宿主可以观察到发现基础设施结束，而不会留下无人监管的活动连接。

## 16. 维护结束后，调用已清理的实例应为 Unavailable

~~~csharp
try
{
    await node.CallPeerAsync<Empty>(router1.Process.Name, new Empty(), stop);
    throw new Exception("Discovery ended before its active connections were cleaned up.");
}
catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
{
}
~~~

CallPeerAsync<Empty> 的泛型参数指定预期回复消息类型。请求用新的 Empty；这里期望在找不到 links 条目时就失败，不会真的等到业务回复。

catch 后的 when 是异常过滤器，只有 RpcException 且 StatusCode 为 Unavailable 才算符合预期。其他状态码、取消或非 RPC 异常不会被误当成清理成功。

结合实现，维护结束清理会移除 links，CallPeerAsync 在无连接时直接抛 Unavailable。不过测试只检查状态码，并未读取内部字典；某些残留但已关闭的链接也可能产生同类不可用错误。因此这是行为层面的清理证据，不是对内部集合内容的独立检查。

Ready 是否仍为真与这个调用能否成功是两件事：历史启动状态不承担当前目标可用性保证。

## 17. 两层 finally 各自完成什么

模拟服务无论正常结束、取消还是抛错，都执行：

~~~csharp
finally
{
    connection.Closed.TrySetResult();
}
~~~

TrySetResult 只把 Closed 任务标记完成。任务成功表达“结束事件已经发生”，不表示连接一直健康或这次调用无错误。

外层测试则执行：

~~~csharp
finally
{
    lifetime.Cancel();
    try
    {
        await maintenance;
    }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
    {
    }
    catch (InvalidDataException) when (changes.Reader.Completion.IsFaulted)
    {
    }
}
~~~

先请求取消，再等待 maintenance，防止测试结束后维护循环仍使用共享依赖。已失败的 Task 可以再次 await，仍会传播同一完成结果，不会重新运行维护。

第一种过滤器接受已请求取消时的取消异常；第二种在发现通道确认故障时接受 InvalidDataException。它没有比较异常的来源身份，因此不宜说它能精确区分所有协议错误与发现错误。其他未匹配异常继续传播。

之后作用域中的三个 WebApplication 按逆序异步释放，停止各自监听。TestServices 的 ID、日志和代理由调用者管理，本文件不释放共享依赖。

## 18. 本文件证明到哪里

本文件用事件等待而非固定睡眠推进场景：Connections 通道通知调用出现，Handshaken 通知协议往返，Closed 通知远端结束，FirstLinks 通知首次门槛完成。传入 stop 约束多数等待；它是协作式取消，不是杀死线程的硬截止。

测试直接覆盖门槛 2、迟到的第三台、低于门槛、代次替换、空列表、单台恢复以及发现故障后的不可用行为。它没有验证真实 ZooKeeper Watch、占号与地址发布、正式 LinkService 的身份校验，也没有穷尽网络抖动重试和所有竞争时序。

源码中的 PASS 在正常断言后、finally 前输出；完整通过仍需要清理正常结束。真实发布和临时身份边界继续看 [StartupPublicationChecks.cs](StartupPublicationChecks.cs.md)。本文没有启动服务或执行测试。
