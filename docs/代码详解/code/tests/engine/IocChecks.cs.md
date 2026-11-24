# IocChecks.cs 代码详解

源码：[code/tests/engine/IocChecks.cs](../../../../../code/tests/engine/IocChecks.cs)

这份测试回答一个很具体的问题：App 的实际依赖注册，能否让一个进程共享同一组服务，同时让两个独立容器互不干扰，并且在失败时仍能写日志？它调用正式的 [ServerServices.cs](../../server/app/ServerServices.cs.md)，只替换需要真实占号和 worker 分配的部分。

生产 App 每进程建立一个根容器。本测试为了比较对象，在同一个测试进程里建立两个容器；这不是建议生产进程建立两个服务根。测试直接调用普通方法，断言失败就抛异常，没有依赖测试框架的特定属性。

## 1. 入口里的类型和资源声明

~~~csharp
internal static async Task RunAsync()
{
    using var firstLogLifetime = new GameLog();
    using var secondLogLifetime = new GameLog();
    await using var first = CreateProvider(firstLogLifetime, 1);
    await using var second = CreateProvider(secondLogLifetime, 2);
~~~

这是 RunAsync 开头的原文片段，代码块后面还会继续执行检查。

internal 表示同一程序集内可调用；static 表示不需要创建 IocChecks 对象。Task 表示调用者可以等待这组检查结束，async 允许方法体使用 await，并不表示自动创建后台线程。[测试入口](Program.cs.md) 在 --ioc 模式等待这个方法。

GameLog 是外部先创建的对象。using var 表示在退出当前作用域时同步调用 Dispose；await using var 表示退出时等待 DisposeAsync。这里右侧 CreateProvider 本身是同步方法：await using 的 await 用于未来释放容器，不是在等待右侧创建。

为什么先声明日志，再声明容器？作用域自动清理按声明逆序进行，因此通常先释放 second、first，再释放 secondLogLifetime、firstLogLifetime。容器退出时发生错误，外部日志仍然可用。稍后测试还会主动提前释放 first，专门观察这个所有权约定。

## 2. ProcessRegistrations：先使用生产注册，再替换测试参数

~~~csharp
private static ServiceCollection ProcessRegistrations(GameLog log)
{
    var services = new ServiceCollection();
    var configuration = TestNodes.LoadConfiguration();
    var options = TestServices.LocalZooKeeperOptions() with { Endpoint = "127.0.0.1:invalid-port", DiscoveryRoot = "/ioc-checks" };
    var idOptions = new IdGeneratorOptions { WorkerRootPath = "/ioc-workers" };
    ServerServices.AddProcessServices(services, configuration, log);
    Check(services.Count(service => service.ServiceType == typeof(ServerConfiguration)) == 1,
        "Process registrations contain multiple configuration services.");
    services.AddSingleton(options);
    services.AddSingleton(idOptions);
    return services;
}
~~~

ServiceCollection 先保存“什么类型用什么对象或工厂创建”的注册描述；它还不是负责解析对象的 ServiceProvider。TestNodes.LoadConfiguration 从测试输出目录的 config/server.yaml 读入完整配置，加载时展开 include。每次调用此方法都显式加载一次，所以两个测试容器拥有不同配置对象。

with 用于记录类型的复制表达式：以 LocalZooKeeperOptions 的结果为基础，创建修改了 Endpoint、DiscoveryRoot 的新选项。其他字段保留。这里的非法端口是有意准备的哨兵值：本段只允许构造代理，不应该连接网络。ZooKeeperProxy 的构造函数校验配置、保存依赖，实际客户端创建在 InitializeAsync 中。

生产注册中与本测试最相关的代码是：

~~~csharp
services.AddSingleton(configuration);
services.AddSingleton(configuration.Group);
services.AddSingleton(configuration.ZooKeeperOptions);
services.AddSingleton(configuration.Identifiers);

// 日志先于配置和容器创建，仍由 App 在所有资源退出后释放。
services.AddSingleton(log);
services.AddSingleton<ZooKeeperProxy>();
services.AddSingleton<IdGenerator>();
services.AddSingleton(provider => provider.GetRequiredService<ZooKeeperProxy>().Process);
services.AddSingleton<ServerNode>();
~~~

AddSingleton(configuration) 注册已有对象；AddSingleton<ZooKeeperProxy>() 则让容器按构造函数依赖创建对象。泛型尖括号里的类型告诉容器服务是什么。单例指同一容器内复用，不是整个操作系统或所有容器只有一个实例。

Count 的 lambda 接收一条注册，比较其 ServiceType 与 typeof(ServerConfiguration)。typeof 取得表示某个类型的 Type 对象。结果必须为 1，防止正式注册里出现多份配置服务。这个断言数的是注册条目，不是在统计已经构造的实例。

最后追加测试 options 和 idOptions。对同一种服务使用 GetRequiredService<T>() 单独解析时，.NET 容器采用最后一条注册；此前条目仍在集合里，没有被删除。这使测试可以保留正式注册流程，又换成适合本地场景的参数。

## 3. CreateProvider：两层 lambda 分别什么时候运行

~~~csharp
private static ServiceProvider CreateProvider(GameLog log, int workerId)
{
    var services = ProcessRegistrations(log);
    services.AddSingleton(new ServerProcess(ProcessType.Router, workerId));
    services.AddSingleton<IdGenerator>(_ => new IdGenerator(token =>
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new SnowflakeIdGenerator(workerId, TimeProvider.System));
    }));
    return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}
~~~

两个容器分别传入 workerId 1、2。这里同时用它构造 Router1、Router2 的测试身份和雪花 worker 编号，只是本用例的便利安排；生产进程编号和 worker 租约不是必须相等的同一种编号。

第一条注册用现成 ServerProcess 替代正式的“从 ZooKeeperProxy.Process 取得已认领身份”的工厂。测试没有先连接 ZooKeeper，所以必须显式给出身份。

第二条注册有两层函数：

- 外层 lambda 的参数 _ 是容器传入的服务提供者，这里不使用。容器第一次解析 IdGenerator 时执行外层函数，创建外层 ID 服务。
- 内层 token => ... 是 ID 服务保存的创建委托。直到 InitializeAsync 调用它，才检查取消并创建 SnowflakeIdGenerator。
- Task.FromResult 将现有生成器包装为已完成的 Task<SnowflakeIdGenerator>，不新建线程，也不去申请真实 worker 租约。

workerId 被内层函数捕获。即使 CreateProvider 已经返回，函数需要的值仍然可用，这就是闭包。每次调用 CreateProvider 有自己的一份捕获状态。

BuildServiceProvider 把描述集合变成可解析服务的容器。ValidateOnBuild 检查可验证的依赖图，ValidateScopes 检查作用域使用；它们不意味着所有服务均已创建，也不会代替异步初始化。工厂内部的业务动作尤其不能靠构建验证全部证明。

## 4. 配置和日志：比较的是对象身份

~~~csharp
var firstLog = first.GetRequiredService<GameLog>();
var secondLog = second.GetRequiredService<GameLog>();
var firstConfiguration = first.GetRequiredService<ServerConfiguration>();
Check(ReferenceEquals(firstConfiguration, first.GetRequiredService<ServerConfiguration>()),
    "YAML configuration is not a singleton in its container.");
Check(!ReferenceEquals(firstConfiguration, second.GetRequiredService<ServerConfiguration>()),
    "Independent containers share their YAML configuration.");
Check(ReferenceEquals(firstConfiguration.Group, first.GetRequiredService<ServerGroup>()),
    "The node startup group does not come from its shared configuration.");
Check(ReferenceEquals(firstLog, firstLogLifetime), "The container replaced the startup logger.");
Check(!ReferenceEquals(firstLog, secondLog), "Independent containers share a logger.");
~~~

GetRequiredService<T>() 按类型取得服务；无法提供时抛异常。ReferenceEquals 比较两个引用是否指向同一对象，区别于“对象内容是否相等”。

第一条配置检查要求同容器两次取值仍是原对象。第二条要求独立容器没有共用配置。第三条进一步要求直接解析的 ServerGroup 就是该配置持有的 Group，不能另外构造一个内容碰巧相同的数量计划。

日志检查也有两层：firstLog 必须就是外部创建的 firstLogLifetime；secondLog 必须与它不同。如果容器偷偷替换日志，启动早期和容器运行阶段就可能各自用一份日志，释放顺序也失去依据。

这些检查不是“配置文件不会改变”的证明；它们检查的是依赖注册中的共享关系。文件修改与内存对象独立性由 [ServerConfigurationChecks.cs](ServerConfigurationChecks.cs.md) 说明。

## 5. ID 服务：先初始化，再比较单例和隔离

~~~csharp
var firstIds = first.GetRequiredService<IdGenerator>();
var secondIds = second.GetRequiredService<IdGenerator>();
await firstIds.InitializeAsync();
await secondIds.InitializeAsync();
Check(ReferenceEquals(firstIds, first.GetRequiredService<IdGenerator>()), "ID service is not shared within its container.");
Check(!ReferenceEquals(firstIds, secondIds), "Independent containers share an ID generator.");
Check(firstIds.GenId() != secondIds.GenId(), "Different worker instances generated the same ID.");
~~~

解析服务只构造 IdGenerator，内部生成器尚未初始化。两个 await 顺序执行：先等第一份初始化结束，再初始化第二份。它们执行上一节保存的本地创建函数，不连接 ZooKeeper。

初始化后，同容器再次解析必须返回 firstIds，跨容器必须不同。最后分别生成一个 ID 并比较字符串；不同 worker 位应使两个样本不同。

这个检查的范围有限：它不是跨进程大规模唯一性证明，也不检查真正的 worker 抢占。它能发现容器错误共享生成器等明显问题。完整的生命周期、取消与并发例子见 [IdGeneratorLifecycleChecks.cs](IdGeneratorLifecycleChecks.cs.md)。

## 6. ZooKeeper 与节点：构造不等于建立连接

~~~csharp
var firstZk = first.GetRequiredService<ZooKeeperProxy>();
var secondZk = second.GetRequiredService<ZooKeeperProxy>();
Check(ReferenceEquals(firstZk, first.GetRequiredService<ZooKeeperProxy>()), "ZK service is not shared within its container.");
Check(!ReferenceEquals(firstZk, secondZk), "Independent containers share a ZK proxy.");
var firstNode = first.GetRequiredService<ServerNode>();
var secondNode = second.GetRequiredService<ServerNode>();
Check(firstNode.Ready && secondNode.Ready, "Router resolved from the actual registrations is not ready.");
Check(firstNode.Name != secondNode.Name && !ReferenceEquals(firstNode, secondNode), "Containers share process identity or node state.");
Check(ReferenceEquals(firstNode, first.GetRequiredService<ServerNode>()), "Node is not shared within its container.");
~~~

ZooKeeper 的两条检查仍然只比较对象。没有调用其正常 InitializeAsync，所以非法端口不会在这里触发连接。

解析 ServerNode 时，容器将配置、Group、代理、ID、日志和 ServerProcess 注入构造函数。Router 的主动连接列表为空，构造期间 TryCompleteFirstLinks 无需等入站连接便完成任务。因此 Ready 应为真，即使这里还没有监听端口或发布地址。

Name 不同要求 Router1、Router2 的身份没有串用；引用不同要求节点自己的连接状态也没有串用。再解析 firstNode 则应返回原对象。如果只验证名称而不比较引用，就不能直接证明服务的实例生命周期。

这里的 Ready 是首次连接要求已达成，不代表已经完成 App 的全部启动步骤。生产宿主还要显式完成监听、发布并记录 PROCESS_READY；本段没有运行宿主。

## 7. 释放第一个容器之后的检查

~~~csharp
await first.DisposeAsync();
Expect<ObjectDisposedException>(() => firstIds.GenId());
await ExpectAsync<ObjectDisposedException>(() => firstZk.InitializeAsync());
Check(IdText.IsValid(secondIds.GenId()), "Disposing one container invalidated another container's ID service.");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
await ExpectAsync<OperationCanceledException>(() => secondZk.InitializeAsync(cancelled.Token));
await first.DisposeAsync();
Check(IdText.IsValid(secondIds.GenId()), "Repeated disposal invalidated another container.");
~~~

容器拥有自己构造的共享服务，所以第一次 DisposeAsync 完成后，firstIds 应拒绝发号，firstZk 应拒绝初始化。变量并没有因释放而消失；仍可用它们调用方法，观察对象是否正确保护已结束的生命周期。

secondIds 应继续生成合法格式的 ID，证明释放一个容器没有通过静态状态破坏另一容器。重复释放 first 后再检查一次，验证幂等清理没有额外影响。

取消源先 Cancel，再把 Token 传入 secondZk.InitializeAsync。代理的第一步等待生命周期信号量，预取消会使它抛 OperationCanceledException，不进入非法端口的客户端创建。这检查了预取消路径，但它本身不能单独充分证明第二个代理未被释放：预取消可能先于 disposed 检查发生。测试对第二份服务隔离的直接行为证据主要来自 secondIds，不能把所有断言都扩大解释成完整隔离证明。

## 8. Expect 与 ExpectAsync 怎样判断“应当失败”

~~~csharp
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
~~~

Action 是无参数、无返回值的委托。() => firstIds.GenId() 把“调用发号”交给 Expect，直到 action() 才执行，字符串返回值在这里丢弃。

T 是泛型参数，where T : Exception 要求它属于异常类型。catch (T) 接受 T 以及派生异常；匹配就返回，不匹配则继续向外传播。若调用根本没抛异常，会执行最后一行，使测试失败。typeof(T).Name 用于拼出错误信息。

~~~csharp
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
~~~

Func<Task> 是无参数、返回任务的函数。异步版本必须 await action()，才能观察保存在失败任务中的异常；只调用而不等待，无法可靠断言异步结果。调用方也要 await ExpectAsync，避免尚未确认结果就进入下一段。

Check 的规则更直接：条件为假时抛 Exception(message)，为真时返回。它们共同把本文件的普通 C# 方法组成了可失败的测试过程。

## 9. 重定向 stderr，观察容器释放之后的日志

~~~csharp
var originalError = Console.Error;
using var error = new StringWriter();
Console.SetError(error);
try
{
    // 外部创建的启动日志必须活过容器，才能记录初始化或释放失败。
    firstLog.Error(ServerLogEvents.ProcessFailed, nameof(ProcessType.Router), detail: "容器退出后的启动错误", bothPrintConsole: true);
~~~

这里保存原 stderr，再临时把错误输出指向内存字符串写入器。first 已被释放，但 firstLog 是外部创建并注册的实例，容器不负责释放它；因此仍应能够输出。

nameof(ProcessType.Router) 在编译时取得规范类型名称。detail: 和 bothPrintConsole: 是命名参数，明确说明哪个实参对应哪个参数。bothPrintConsole: true 要求日志同时镜像到终端，便于 StringWriter 收集。

Console.SetError 改的是进程级共享状态，不只是某个对象的字段。因此外层 finally 必须恢复它。本测试并未把这个全局重定向设计成可以和其他改 stderr 的用例任意并行。

## 10. 身份未认领导致解析失败，外部日志仍须可用

~~~csharp
using var failedLog = new GameLog();
var registrations = ProcessRegistrations(failedLog);
await using (var failed = registrations.BuildServiceProvider())
{
    Expect<InvalidOperationException>(() => failed.GetRequiredService<ServerNode>());
}

failedLog.Error(ServerLogEvents.ProcessFailed, nameof(ProcessType.Router), detail: "身份未认领导致初始化失败", bothPrintConsole: true);
~~~

这一次只调用 ProcessRegistrations，没有追加测试 ServerProcess。解析 ServerNode 会回到正式注册的身份工厂：

~~~csharp
provider => provider.GetRequiredService<ZooKeeperProxy>().Process
~~~

该代理未建立有效会话、也未认领身份，访问 Process 应抛 InvalidOperationException。异常发生在服务解析期间，不等同于 BuildServiceProvider 必须失败。

await using (...) 的代码块结束时容器已经释放，随后仍用外部 failedLog 记录失败。这与前面的 firstLog 场景分别观察了“正常拥有服务的容器已退出”和“服务解析中途失败的容器已退出”。

## 11. 把日志文本解析成证据，而不是只看有没有输出

~~~csharp
var details = new HashSet<string?>();
foreach (var row in error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
{
    using var document = JsonDocument.Parse(row);
    details.Add(document.RootElement.GetProperty(ServerLogFields.Detail).GetString());
}

Check(details.Contains("容器退出后的启动错误") && details.Contains("身份未认领导致初始化失败"),
    "Startup logging was lost after container failure/disposal.");
~~~

ToString 取得收集的全部输出，Split 按换行拆开并去掉空行。foreach 逐行处理，每行都必须能作为 JSON 解析；如果日志格式损坏，JsonDocument.Parse 会直接抛异常。

HashSet<string?> 保存不同的 detail 文本。问号表示允许空引用，因为 GetString 的结果可能为 null。这里集合只关心某段文本是否出现，不要求日志条数、顺序或重复次数。

using var document 位于循环体内，每轮离开该循环体时释放当前 JSON 文档。最终两个 Contains 都必须为真；&& 是短路与，左侧不成立就无需检查右侧。若容器过早释放了外部日志，两条所需消息至少一条可能缺失，测试便失败。

不论解析或断言是否成功，外层 finally 都执行 Console.SetError(originalError)，恢复原输出位置。

## 12. 最后调用的检查、释放顺序与覆盖边界

~~~csharp
await ZooKeeperProxyChecks.CheckSessionAsync(secondLog);
await IdGeneratorLifecycleChecks.RunAsync();
Console.WriteLine("PASS actual IoC registrations, shared instances, independent containers, asynchronous disposal and startup error logging.");
~~~

最后继续等待会话检查和 ID 生命周期检查。[ZooKeeperProxyChecks.cs](ZooKeeperProxyChecks.cs.md) 的 CheckSessionAsync 使用模拟会话事件、非法端口和本机临时监听器，不需要外部 ZooKeeper 完成真实会话；它仍可能建立本地 TCP 连接，不能简单说成所有动作都只在内存中。

PASS 只说明程序已走过此前显式断言。之后还要退出作用域并完成自动释放；释放若失败，RunAsync 仍可能失败。first 前面已显式释放，作用域退出再次释放属于幂等清理验证的一部分。

本文件直接检查实际注册的实例共享、不同容器的若干隔离行为、异步释放和错误日志存活。它没有启动生产 App、完成真实占号或分配真实 worker，也没有证明所有并发解析与释放竞争。阅读和文档核对不等于本轮已运行这些检查。
