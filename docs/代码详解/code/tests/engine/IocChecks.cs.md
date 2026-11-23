# IocChecks.cs 代码详解

[源码 IocChecks.cs](../../../../../code/tests/engine/IocChecks.cs)

## 用真实注册建两个容器

引擎检查入口遇到 `--ioc` 时等待 `IocChecks.RunAsync()`。`RunAsync` 在同一个检查进程中创建两份外部拥有的 `GameLog`，再调用 `CreateProvider(log, workerId)` 搭建两个容器。这两个容器用计划身份 `Router1`、`Router2` 检查单例的作用范围。生产 App 每进程组装一个容器。此处用两个容器为同一批类型造出可比较的对象。 [检查程序入口](Program.cs.md) · [生产注册](../../server/app/ServerServices.cs.md)

1. `ProcessRegistrations(log)` 从测试程序输出目录的 `config/server.yaml` 加载配置，使用正式的 `ServerServices.AddProcessServices(services, configuration, log)` 注册。`services.Count(service => service.ServiceType == typeof(ServerConfiguration))` 断言集合里恰有一个配置注册。这里 `service => ...` 是 lambda，接收每条注册并判断其服务类型。
2. 测试在后面追加 ZooKeeper 和 ID 的测试选项。ZooKeeper 的 `Endpoint` 刻意用 `127.0.0.1:invalid-port`，发现根是 `/ioc-checks`，ID worker 根是 `/ioc-workers`。`with { Endpoint = ... }` 基于选项记录生成修改指定字段的副本。
3. `CreateProvider` 再注册确定的 `ServerProcess(ProcessType.Router, workerId)`，并追加 `IdGenerator` 的测试工厂。.NET 依赖注入单独解析同一类型时采用最后一条注册，因此这里用测试身份覆盖从 ZooKeeper 取得身份的工厂，也用本地 worker 工厂覆盖正式 ID 服务。工厂先检查取消令牌，再用 `Task.FromResult` 返回已完成的生成器任务。worker ID 分别为 1、2。 [测试配置加载](TestNodes.cs.md) · [本地测试依赖](TestServices.cs.md)
4. `BuildServiceProvider` 启用 `ValidateOnBuild` 和 `ValidateScopes` 来检查容器构建和作用域使用。拿到容器之后，测试还要显式解析服务并初始化 ID。ZooKeeper 代理的构造阶段可以在未登录的情况下完成。

## RunAsync 怎么证实共享与隔离

`GetRequiredService<T>()` 用泛型参数 `T` 指定要解析的服务，无法提供时抛异常。`ReferenceEquals(a, b)` 问的是两个变量是否指向内存里的同一个对象，适合检查单例范围。

| 检查对象 | 准备和断言 |
| --- | --- |
| `ServerConfiguration`、`ServerGroup` | 第一个容器两次解析配置得到同一对象，第二个容器得到不同配置对象。第一个容器直接解析的组与该配置持有的 `Group` 是同一个对象。 |
| `GameLog` | 第一个容器解析的就是预先创建的 `firstLogLifetime`，两容器解析的日志引用不同。 |
| `IdGenerator` | 分别等待初始化。第一容器重复解析同一实例，两容器对象引用不同，从不同 worker 生成的样本 ID 不相等。 |
| `ZooKeeperProxy` | 第一容器重复解析得到同一代理，两容器的代理引用不同。 |
| `ServerNode` | 两个节点分别持有不同名称并且引用不同。第一容器再解析仍命中同一节点。当前配置的 `Router` 主动连接列表为空，节点构造时就能完成首次门槛，所以二者的 `Ready` 都应为真。 |

节点在此处只完成构造，`Ready` 来自首次连接门槛，`Router` 的空依赖无需实际建连。这个场景也没有运行 App 的监听或地址发布。 [进程拓扑与门槛](../../server/engine/configuration/ServerConfiguration.cs.md) · [节点就绪](../../server/engine/ServerNode.cs.md)

## 释放一个容器后还能做什么

测试显式 `await first.DisposeAsync()`，再要求 `firstIds.GenId()` 和 `firstZk.InitializeAsync()` 抛 `ObjectDisposedException`。第二个容器的 `GenId()` 仍应得到 `IdText.IsValid` 判定为合法的 ID。构造一个已取消的令牌让第二个 ZooKeeper 代理初始化，预期 `OperationCanceledException`，据此确认第二个代理还可以进入它自己的取消检查。随后再次释放第一个容器，第二个仍能正常生成 ID。

`Expect<T>(Action)` 和 `ExpectAsync<T>(Func<Task>)` 分别检查同步和异步调用。`where T : Exception` 把泛型参数限制为异常类型，`catch (T)` 捕获预期异常或派生类型。异步版本在 `try` 中等待任务，所以能检查任务完成时传播的异常。

## 启动日志必须晚于容器释放

`Console.SetError(error)` 暂时把 stderr 指向 `StringWriter`。测试向已释放的第一个容器原来使用的 `firstLog` 写入“容器退出后的启动错误”。随后建立第三份真实注册，此次没有追加测试身份。解析 `ServerNode` 时会追到正式身份工厂，访问未认领的 `ZooKeeperProxy.Process`，于是要求抛 `InvalidOperationException`。释放这一容器后，外部的 `failedLog` 仍须能输出“身份未认领导致初始化失败”。采集的每一行用 `JsonDocument` 解析，取 `detail` 放进 `HashSet<string?>`，最后断言两段文本同时存在。`finally` 恢复原 stderr。 [身份解析与日志所有权](../../server/app/ServerServices.cs.md) · [终端错误镜像](../../server/engine/GameLog.cs.md)

最后另行等待 `ZooKeeperProxyChecks.CheckSessionAsync(secondLog)` 和 `IdGeneratorLifecycleChecks.RunAsync()`。会话检查用模拟状态与本地监听端口覆盖初始化、取消及会话丢失，ID 生命周期检查还涉及并发发号和租约失效。这些调用都无需外部 ZooKeeper 服务完成真实占号。方法作用域结束时，按声明的反序清理，`await using` 等待两个容器的异步释放，外层两个 `using var` 随后释放日志。即使第一个容器之前已经显式释放，离开作用域仍会调用其释放方法。 [会话检查](ZooKeeperProxyChecks.cs.md) · [ID 生命周期检查](IdGeneratorLifecycleChecks.cs.md)
