# StartupPublicationChecks.cs 代码详解

源码：[code/tests/engine/StartupPublicationChecks.cs](../../../../../code/tests/engine/StartupPublicationChecks.cs)

这份检查把真实 ZooKeeper 与正式 LinkService 接在一起，验证一个关键启动顺序：先取得临时身份，接收端完成就绪并启动监听，再发布地址。主动连接者可以只有有效临时占号，尚未发布自己的地址。

与 [DiscoveryLinkChecks.cs](DiscoveryLinkChecks.cs.md) 的模拟发现列表不同，本文件会连接真实 ZooKeeper、创建临时与持久节点，并实际启动本机 HTTP/2 gRPC 监听。本文只解释源码，没有执行这些外部操作。

## 1. 三种状态不能合成一个“已启动”

本场景涉及三个独立条件：

- **已占号**：ZooKeeper 中存在符合规范的临时身份节点，可以证明该名字由一个会话持有；节点数据可以为空。
- **节点 Ready**：本端的 FirstLinks 已成功完成，入站接收端满足引擎就绪门槛。
- **已发布地址**：临时节点数据写入可拨号地址，服务发现才把它返回给其他进程。

Router 没有主动依赖，构造节点后即可满足 FirstLinks；但监听端口仍要由 WebApplication 启动，地址仍要由 RegisterProcessAsync 发布。不能从 Ready 一个属性推断后两件事已经发生。

对于有依赖的进程，允许“未发布地址但已占号的调用者”主动拨号非常重要。否则会出现本端想先连依赖才能发布、依赖却要求本端先发布才能接入的相互等待。

## 2. 入口先等待本地就绪检查，再准备真实发现根

~~~csharp
public async Task RunAsync(CancellationToken stop)
{
    await new StartupReadinessChecks(services).RunAsync(stop);
    var configured = ZooKeeperProxyChecks.LoadOptions();
    var options = configured with { DiscoveryRoot = configured.DiscoveryRoot + "/startup-check-" + Guid.NewGuid().ToString("N") };
    await using var proxy = new ZooKeeperProxy(options, services.Log);
    await proxy.InitializeAsync(stop);
~~~

类通过主构造函数接收 TestServices，RunAsync 返回 Task 供调用者等待。第一句先创建并等待 StartupReadinessChecks；其本地场景成功后才进入真实 ZooKeeper 部分。不能把它理解成同时启动两组检查。

LoadOptions 调用 ServerConfiguration.LoadSelected()，从 EngineEnvironment.Config 指定的源配置读取 ZooKeeperOptions。这里需要真实可用的服务连接参数。

with 创建选项记录的副本，只更改 DiscoveryRoot。Guid.NewGuid().ToString("N") 生成无连字符的新标识，拼出本次独立 startup-check-* 子根。原配置对象不变，后面只清理这次子根。

构造 proxy 只保存参数和依赖；await InitializeAsync(stop) 才等待真实会话建立。await using 安排方法退出时异步释放这个代理，后面还会显式提前释放以控制清理顺序。

[测试入口](Program.cs.md) 为 --startup-publication 提供较长的外部检查取消期限。所有步骤共享传入的 stop，并不是每个 await 重新获得一段完整期限。

## 3. 两个 ZooKeeper 客户端各自拥有独立会话

~~~csharp
var owner = new ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, new WatcherStub());
var scene = new ServerProcess(ProcessType.Scene, 1);
var absent = new ServerProcess(ProcessType.Scene, 2);
var persistent = new ServerProcess(ProcessType.Scene, 3);
~~~

proxy 是正式 ZooKeeperProxy，负责 Router 的认领、发布、发现和入站身份查询。owner 是额外创建的原始 ZooKeeper 客户端，用于制造其他进程的节点；它不是 proxy 内部客户端的另一个变量名。

三个 ServerProcess 只是不可变身份值。创建 Scene1 对象不会自动在 ZooKeeper 中占号；之后需要显式 createAsync。Scene2 故意一直不创建节点。Scene3 则会创建为持久节点，模拟“名字存在但没有临时会话所有者”的坏身份。

owner 的事件接收器是：

~~~csharp
private sealed class WatcherStub : Watcher
{
    public override Task process(WatchedEvent evt) => Task.CompletedTask;
}
~~~

它继承库的 Watcher，并重写 process；参数 evt 在这里不使用，直接返回已完成任务。这个空实现只满足客户端事件接口，并不承担正式 ZooKeeperSession 的失效管理职责，也不表示 owner 没有网络行为。

## 4. 认领 Router 后，空地址身份已经有效

~~~csharp
var process = await proxy.ClaimProcessAsync(ProcessType.Router, stop);
Check(await proxy.IsProcessClaimedAsync(process.Name, stop),
    "A reserved ephemeral identity must be valid before its address is published.");
Check(!await proxy.IsProcessClaimedAsync(absent.Name, stop), "An absent identity was accepted.");
Check(!await proxy.IsProcessClaimedAsync(nameof(ProcessType.Scene), stop), "A type without an instance number was accepted.");
~~~

ClaimProcessAsync 为 Router 类型从 1 开始尝试编号，在本次独立根中创建空数据临时节点，返回实际认领的 ServerProcess。此时还没有监听地址，也没有调用发布方法。

第一条断言立即检查身份有效，明确发生在发布之前。后两条分别要求不存在的 Scene2 返回 false，以及纯类型名 Scene 返回 false。nameof(ProcessType.Scene) 取得的是类型名，没有实例编号，因此不能当实例身份。

Check(await ...) 先等待 Task<bool> 得到布尔值，再调用 Check。!await 表示对等待结果取反，不是取消异步操作。

IsProcessClaimedAsync 的核心实现是：

~~~csharp
var stat = await Client.existsAsync(ProcessPath(process)).WaitAsync(stop).ConfigureAwait(false);
session.CheckActive();
return stat is not null && stat.getEphemeralOwner() != 0;
~~~

先查节点元数据，再确认代理会话仍有效。stat 非空表示路径存在；ephemeralOwner 非零表示它由某个会话作为临时节点持有。这里没有要求节点数据中存在地址，也没有把地址发布状态当成身份合法条件。

它检查临时身份的存在，不是密码签名或完整应用鉴权协议。本测试关注项目约定的会话占号边界。

## 5. 名字正确的持久节点仍不能作为活进程身份

~~~csharp
await owner.createAsync(PathFor(persistent), [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT).WaitAsync(stop);
Check(!await proxy.IsProcessClaimedAsync(persistent.Name, stop), "A persistent node was accepted as a live process identity.");
~~~

PathFor 在方法末尾定义为：

~~~csharp
string PathFor(ServerProcess process) => options.DiscoveryRoot + "/server-" + process.Name;
~~~

这是捕获 options 的局部表达式函数，为测试构造节点路径。参数 process 是该局部函数自己的参数，不等于外层同名 Router 变量；传入 persistent 时产生 Scene3 的路径。

createAsync 的 [] 是空字节数组，表示节点没有地址数据。CreateMode.PERSISTENT 创建持久节点，不会随 owner 会话自然结束而自动消失。OPEN_ACL_UNSAFE 是这里采用的库 ACL 常量，测试没有验证 ACL 权限策略。

WaitAsync(stop) 让调用者可以取消等待库返回的任务；它不表示能够撤销服务端已经完成的创建。后续清理会处理本次根中的实际节点。

持久节点的 ephemeralOwner 为 0，因此即使路径和名字都正确，也应被身份检查拒绝。如果只用“路径存在”判断存活，此断言就会失败。

## 6. 用认领身份创建真实接收节点

~~~csharp
var configuration = TestNodes.LoadConfiguration();
var node = new ServerNode(process, configuration, services.Startup, proxy, services.Ids, services.Log);
await using var app = await StartAsync(node, proxy, stop);
using var channel = GrpcChannel.ForAddress(app.Urls.Single());
var client = new ServerLink.ServerLinkClient(channel);
~~~

ServerNode 使用已认领的 Router 身份，注入正式 proxy 和共享测试 ID、日志。configuration 提供类型连接关系，services.Startup 提供本地测试门槛；Router 没主动目标，所以本场景不会等待其他类型。

StartAsync 返回已经启动的 WebApplication。右边 await 等监听完成，左边 await using 保证退出 try 作用域时异步释放应用。

GrpcChannel.ForAddress 创建访问这个端点的频道，ServerLinkClient 是生成的 gRPC 客户端。这里只建立调用工具，双向流调用尚未开始；后面的 client.Connect 才创建具体 RPC。

using var channel 管理频道本身。一个频道可承载多个调用，本场景先用合法 Scene1，再用两个坏身份分别发起调用。

## 7. StartAsync 使用正式 LinkService，而不是模拟远端

~~~csharp
private static async Task<WebApplication> StartAsync(ServerNode node, ZooKeeperProxy proxy, CancellationToken stop)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
        listen => listen.Protocols = HttpProtocols.Http2));
    builder.Services.AddGrpc();
    builder.Services.AddSingleton(node);
    builder.Services.AddSingleton(proxy);
    var app = builder.Build();
    app.MapGrpcService<LinkService>();
    await app.StartAsync(stop);
    return app;
}
~~~

Kestrel 在回环地址端口 0 监听，由操作系统分配空闲端口。ConfigureKestrel 和 Listen 的 lambda 分别修改服务器选项与此监听端点选项；Http2 明确使用 HTTP/2。

AddGrpc 注册框架支持。AddSingleton(node)、AddSingleton(proxy) 注册已有实例，测试 App 不重新构造它们；外层测试仍拥有 proxy 的生命周期。MapGrpcService<LinkService>() 使用正式服务类型，其 Connect 会调用注入 node 的 AcceptConnectAsync。

这段辅助方法没有运行完整生产 Program：没有启动整个进程组，也没有应用完整业务流程。若 StartAsync 在返回前失败，调用者还没取得 app，源码也没有在此辅助方法内部显式 catch 后释放；不要把调用者的 await using 描述成覆盖了一切构建失败。

## 8. Scene1 只有临时占号，尚未发布地址

~~~csharp
await owner.createAsync(PathFor(scene), [],
    ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(stop);
await using var discovery = proxy.WatchProcessesAsync(stop).GetAsyncEnumerator(stop);
Check(await discovery.MoveNextAsync() && discovery.Current.Count == 0,
    "An initializing process was discoverable before publishing its address.");
~~~

与 Scene3 不同，Scene1 使用 CreateMode.EPHEMERAL。它由 owner 的会话持有，但数据仍为空，没有调用任何地址发布。

WatchProcessesAsync 返回异步序列；GetAsyncEnumerator 取得枚举器，MoveNextAsync 等待下一份快照。成功返回 true 后，Current 才是这份列表。&& 短路避免枚举已结束时仍把 Current 当新快照使用。

获取枚举器不等于立即把所有快照读出来；第一次 MoveNextAsync 才推进异步迭代。await using 保证离开作用域时调用枚举器的 DisposeAsync。

此刻根中有空数据 Router、空数据 Scene1、空数据持久 Scene3。发现读取代码遇到 data.Data.Length == 0 直接忽略，因此初始可拨号列表应为空。这个断言证明空地址记录隐藏；对于当前空数据 Scene3，发现代码还没有走到“地址非空后检查临时属性”的分支。持久身份拒绝已经由前面的 IsProcessClaimedAsync 单独验证。

## 9. 先确认 Ready，再发布本端实际地址

~~~csharp
Check(node.Ready, "Router was not ready after its initialization completed.");
await proxy.RegisterProcessAsync(process, app.Urls.Single(), stop);
do
{
    Check(await discovery.MoveNextAsync(), "Discovery ended before address publication.");
}
while (discovery.Current.Count == 0);
~~~

node.Ready 必须为真，才能把它作为可接入的接收节点。RegisterProcessAsync 验证传入身份就是 proxy 自己认领的身份，再把实际地址写进现有临时节点数据，而不是另建一个独立身份。

发布后 Watch 不一定立即在调用栈里同步交付新列表，所以用 do/while 继续推进枚举，直到出现非空快照。do 表示至少执行一次循环体，while 在每次取到快照后判断是否还为空。

每次 MoveNextAsync 都必须返回 true；若发现序列提前结束，就明确失败。取消由传入 stop 限制，不使用固定延迟猜测 Watch 完成时机。

为什么只改变节点数据也能触发发现？WatchProcessesAsync 不仅监听父节点成员变化，还在读每个节点时安装数据 Watch。父节点子项列表未变，地址从空变为非空仍能被观察。

## 10. 发布后的列表只能包含已经发布的 Router

~~~csharp
Check(discovery.Current.Count == 1 && discovery.Current[0].Process == process,
    "Publication exposed an initializing caller or lost the ready receiver.");
~~~

数量必须恰好为 1，且这一项的身份等于刚认领的 Router。ServerProcess 是 record，== 比较身份的成员值，不要求同一个引用。

左侧 Count == 1 为真才访问索引 0，避免空列表访问越界。这个组合同时拒绝“没有看到 Router”和“把未发布的 Scene1 也返回出来”。

测试没有要求列表对象本身与先前快照为不同引用，也没有对所有发现字段逐一断言；本段关注发布边界和身份。地址是否能通信由下一段实际连接补充。

## 11. Headers 放在 gRPC 元数据，Hello 放在数据帧

~~~csharp
private Metadata Headers(string peer) => new()
{
    { TransportProtocol.PeerHeader, peer },
    { TransportProtocol.LinkHeader, services.Ids.GenId() }
};
~~~

=> 表示这个方法直接返回一个表达式。new() 根据返回类型推断为 Metadata；花括号中的两项是集合初始化器，分别添加身份键值和链路 ID 键值。

TransportProtocol 提供已有元数据键常量，services.Ids.GenId() 为这一次调用生成新的链路 ID。元数据属于 gRPC 调用头，不是 Frame 的字段。

正式 LinkService 读取这两个头，传给 AcceptConnectAsync。随后对端还要在流中发送 Hello，内部 RunLinkAsync 要求 Hello 与声明的 peer 一致。头和首帧分别参与入口校验及流内握手，不能只发其中一个就视为完整接入。

## 12. 合法但未发布的 Scene1 能完成入站握手

~~~csharp
using (var connection = client.Connect(Headers(scene.Name), cancellationToken: stop))
{
    await connection.RequestStream.WriteAsync(new Frame { Hello = scene.Name }, stop);
    Check(await connection.ResponseStream.MoveNext(stop) && connection.ResponseStream.Current.Hello == process.Name,
        "A ready process rejected an unpublished caller with a valid reserved identity.");
}
~~~

Connect 创建双向流调用；cancellationToken: 是命名参数。客户端 RequestStream 向 Router 写入，ResponseStream 读取 Router 返回的帧。

先发 Hello = Scene1，再等一帧响应，并要求它的 Hello 等于 Router 的实际名字。new Frame { ... } 是对象初始化器，构造消息后设置属性。只有收到正确响应才通过，单纯建好频道不算握手成功。

正式 AcceptConnectAsync 依次校验链路 ID 和类型方向、查询调用方临时身份、检查接收端 Ready，然后进入 RunLinkAsync。Scene → Router 是允许的方向；Scene1 有临时节点；Router 已就绪，所以应成功。

此时 Scene1 从未发布地址。接收端查询的是占号元数据，而不是可拨号发现列表，因此不会因为调用方不可被发现而拒绝它。这正是本场景最重要的正向断言。

using 块退出会释放这次 gRPC 调用，结束它持有的流资源。它没有等待一个单独的“服务端已退出”通知，本文件后面的关注点是其他调用的身份拒绝。

## 13. 不存在身份和持久身份都应返回 Unavailable

~~~csharp
await ExpectUnavailableAsync(client, absent.Name, stop);
await ExpectUnavailableAsync(client, persistent.Name, stop);
~~~

两个名字都属于 Scene 类型，方向正确，链路 ID 也由同一个合法生成器产生。因此期望失败的原因集中在身份占号：Scene2 没节点，Scene3 是持久节点。

辅助方法为：

~~~csharp
private async Task ExpectUnavailableAsync(ServerLink.ServerLinkClient client, string peer, CancellationToken stop)
{
    using var connection = client.Connect(Headers(peer), cancellationToken: stop);
    try
    {
        await connection.RequestStream.WriteAsync(new Frame { Hello = peer }, stop);
        await connection.ResponseStream.MoveNext(stop);
        throw new Exception("A connection without receiver readiness or a valid claimed identity was accepted.");
    }
    catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
    {
    }
}
~~~

写入和等待响应都放在 try 内，因为 gRPC 失败可能在写入或下一次读取时被观察。如果两步都没有抛预期错误，就主动抛 Exception；即使 MoveNext 返回 false，仍会走到该 throw，因此“静默结束流”也不算正确拒绝。

catch 的 when 是异常过滤器，只接受 RpcException 且状态码为 Unavailable。其他 RPC 状态码、取消或其他异常继续向上抛，不能把任何失败都当成身份验证成功。

这里错误消息提到接收方未就绪，但这两次实际调用的 Router 已经 Ready；它们直接覆盖的是坏身份。不能因为消息写了 readiness，就宣称这两个真实网络场景还检验了未就绪拒绝。

## 14. 开头那组本地就绪测试补充哪些条件

RunAsync 开头等待 [StartupReadinessChecks.cs](StartupReadinessChecks.cs.md)，该文件依次覆盖首次多实例门槛、零门槛、已达标后依赖全断开的等待、无主动依赖和延迟入站 Hello。

因此本入口组合了两类证据：本地流精确控制就绪时序；本文件真实网络场景验证临时占号和地址发布。此处的 Router 没有主动依赖，不能单独覆盖“有依赖接收端尚未 Ready 时”的所有网络竞争时序。

本测试也没有执行完整 App 的 PROCESS_READY 日志断言。生产顺序要求最后发布地址并记录该事件，本文件直接观察的是发布前后的发现可见性和接入结果。

## 15. 正常路径先停监听，再进入作用域清理

~~~csharp
await app.StopAsync(stop);
Console.WriteLine("PASS startup publication: hidden reservations, claimed identity checks, readiness gate and ready endpoint discovery.");
~~~

StopAsync 请求停止应用监听并等待停止。之后仍会执行 app 的 DisposeAsync，停止和释放不是同一个调用。

这些局部资源声明在外层 try 内，退出该作用域时先释放后来声明的 discovery 枚举器，再释放 channel，最后异步释放 app。即使前面某个断言抛异常，作用域资源仍会尝试清理。

PASS 位于这些隐式清理完成之前，也位于外层 ZooKeeper 清理之前；看见打印行不能单独等同于整组 RunAsync 正常返回。

## 16. 为什么 finally 先释放 proxy，再让 owner 删除节点

~~~csharp
finally
{
    await proxy.DisposeAsync();
    try
    {
        var children = await owner.getChildrenAsync(options.DiscoveryRoot);
        foreach (var child in children.Children)
        {
            await owner.deleteAsync(options.DiscoveryRoot + "/" + child);
        }

        await owner.deleteAsync(options.DiscoveryRoot);
    }
    finally
    {
        await owner.closeAsync();
    }
}
~~~

proxy 持有 Router 的临时身份。先释放代理，使它的会话和临时身份退出，再由仍活动的 owner 枚举本次根中的剩余节点。

owner 自己持有 Scene1 临时节点，Scene3 又是持久节点，所以不能只依靠关闭 proxy 让一切自动消失。删除所有子节点后再删除根，是因为 ZooKeeper 不能直接删除非空父节点。

最内层 finally 保证只要进入这个删除 try，即使列举或删除失败，也会尝试关闭 owner。这里没有使用 stop 等待清理 API，因此不能把调用方的取消期限解释成整个清理过程的硬上限。

还要注意真实的失败边界：proxy.DisposeAsync 写在内层 try 之前，若它本身抛错，后面的 owner.closeAsync 所在 finally 尚未进入；此外 proxy 初始化、owner 创建发生在外层 try 之前。源码没有覆盖所有准备阶段的清理失败组合，文档不能把这种结构说成“任何异常都保证两个会话关闭”。

proxy 已由 await using 声明，方法作用域退出还会再次释放；其幂等释放使显式提前释放和自动清理可以并存。TestServices 的 ID 和日志由调用者拥有，本文件不释放它们。

## 17. Check 与本文件的实际边界

Check 接收 bool 和失败消息，条件为假就抛 Exception。它不会等待异步工作，所以调用方先 await 查询或 MoveNext，得到结果后再交给它判断。

本文件直接验证空地址临时身份有效但不可发现、缺失或持久身份无效、发布后只出现已发布接收端、未发布的合法调用者能完成正式入站 Hello。它没有启动完整生产 App、多机进程组、真实业务处理，也没有测试所有会话过期和发布失败时序。

测试创建的随机发现子根和本机端点只服务于这个检查；没有生成生产配置快照或修改源 YAML。阅读本文无需执行它，真正通过与否必须以获准运行后的执行结果为准。
