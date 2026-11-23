# StartupPublicationChecks.cs 代码详解

源码：[code/tests/engine/StartupPublicationChecks.cs](../../../../../code/tests/engine/StartupPublicationChecks.cs)

## 职责与执行条件

`RunAsync(stop)` 先执行本地 `StartupReadinessChecks`，再连接真实 ZooKeeper 并启动正式 `LinkService` 的本机 gRPC 服务，验证“临时占号、监听就绪、发布地址”之间的边界。ZooKeeper 配置来自 `ServerConfiguration.LoadSelected()`；每次检查在原发现根下创建带 GUID 的 `startup-check-*` 子根。

推荐把它放在 [DiscoveryLinkChecks.cs](DiscoveryLinkChecks.cs.md) 后读：前者用内存发现列表和模拟接收端，本文件使用真实 ZooKeeper 和正式入站入口，补齐“调用者持有的身份能否被信任为当前活进程”的检查。

## 先区分三个状态和两个客户端

| 状态 | 在哪里体现 | 含义 |
| --- | --- | --- |
| 已占号 | ZooKeeper 中存在临时身份节点，数据仍为空 | 有效会话持有这个实例名，但别人还不能根据它拨号 |
| 已监听并就绪 | 本机 WebApplication 已监听，节点首次门槛已满足 | 具备接收入站的条件；此时地址仍可尚未发布 |
| 已发布 | 临时节点写入实际服务地址 | 服务发现会返回该实例，其他进程可以选择它为目标 |

`proxy` 是生产 `ZooKeeperProxy`，负责测试中的 Router 身份、发现与发布。`owner` 是另建的原始 ZooKeeper 客户端，专门制造 Scene 的临时节点和永久节点，用于构造输入。两者有各自的会话，不是两个变量指向同一客户端。

```csharp
var options = configured with
{
    DiscoveryRoot = configured.DiscoveryRoot + "/startup-check-" + Guid.NewGuid().ToString("N")
};
```

这里的 record `with` 生成一份仅改变发现根的配置，原配置不变。唯一子根把测试身份与日常运行隔离，最后也只清理这个子根。`WatcherStub` 对原始客户端的事件直接返回 `Task.CompletedTask`，不承担生产会话监控职责。

## 主要流程

1. Router 通过 `ClaimProcessAsync` 取得临时身份。即使地址为空，`IsProcessClaimedAsync` 也应认可它。不存在的 Scene2、没有编号的 Scene 类型名，以及由原始客户端创建的永久 Scene3 节点都不能被当成有效活进程。
2. 使用认领身份创建 `ServerNode` 并在随机回环端口启动 HTTP/2 `LinkService`。另由原始客户端创建地址为空的 Scene1 临时节点。
3. 开启发现枚举，初始快照应为空：已占号但尚未发布地址的 Router 和 Scene 都不能被拨号。
4. 确认无主动依赖的 Router 已就绪，然后注册其监听地址。发现快照应只出现这一台 Router，未发布的 Scene 仍隐藏。
5. Scene1 带合法占号、但没有发布地址，仍可主动连接已就绪的 Router 并完成 Hello。不存在身份和永久节点身份的连接必须收到 `Unavailable`。

## 身份校验：节点存在还不够

测试准备的三种 Scene 身份可作对照：

| 身份 | ZooKeeper 中的情况 | `IsProcessClaimedAsync` / 入站结果 |
| --- | --- | --- |
| Scene1 | 原始客户端创建的临时节点，地址为空 | 可作为主动连接者 |
| Scene2 | 没有对应节点 | 不认可，连接返回 `Unavailable` |
| Scene3 | 有节点，但类型是永久节点 | 不认可，连接返回 `Unavailable` |

另外单独检查 `Scene` 这种不带编号的类型名，要求身份查询返回 false。`PathFor` 把本次发现根与 `server-` 前缀、规范实例名组成节点路径，只用于本测试准备及清理数据。

Router 自己也是先占号，随后立即检查 `IsProcessClaimedAsync` 为 true。这个断言发生在发布前，说明校验的是有效临时身份，不是“数据里已有地址”。生产端通过临时节点所属会话等信息识别占号，永久节点即使名字正确也不满足要求。

## 发布前后的发现快照

```csharp
await using var discovery = proxy.WatchProcessesAsync(stop).GetAsyncEnumerator(stop);
```

这段从异步序列取得枚举器。`MoveNextAsync()` 等待下一份快照，成功后读取 `Current`；`await using` 在离开作用域时异步释放枚举器，让 Watch 生命周期能够收尾。

第一次取快照时，Router 与 Scene1 都有身份节点但数据为空，因此列表必须为空。之后将 Router 的实际监听地址写入 ZooKeeper，再持续取快照直到非空：

```csharp
do
{
    Check(await discovery.MoveNextAsync(), "Discovery ended before address publication.");
}
while (discovery.Current.Count == 0);
```

这里不用固定延迟猜测 Watch 何时生效，而是等待状态可见。最终要求恰好一个成员，且身份等于 Router；Scene1 仍未发布，不能混入可拨号列表。循环本身没有独立超时，取消来自传入的 `stop`。

## 未发布的调用方怎样连接

`StartAsync` 把同一个 `node` 和 `proxy` 作为已有实例注册到测试 App，再映射正式 [LinkService](../../server/engine/LinkService.cs.md)。实例资源仍由外层测试作用域管理；测试 App 只负责监听与处理调用。

```csharp
using (var connection = client.Connect(Headers(scene.Name), cancellationToken: stop))
{
    await connection.RequestStream.WriteAsync(new Frame { Hello = scene.Name }, stop);
    Check(await connection.ResponseStream.MoveNext(stop) &&
        connection.ResponseStream.Current.Hello == process.Name,
        "A ready process rejected an unpublished caller with a valid reserved identity.");
}
```

在调用方视角，`RequestStream` 用于发往 Router，`ResponseStream` 用于读取 Router 回复。先发送 Scene1 的 Hello，再检查收到 Router 的 Hello，才证明应用层握手完成；仅创建 `connection` 对象不算握手成功。

这个场景刻意不发布 Scene1 地址。它验证主动方只需持有有效临时占号；接收方必须已经就绪，自己的可发现地址则由发布流程提供。若强制主动方也先发布地址，就会破坏“连接依赖完成后才发布”的启动顺序。

## 关键辅助方法

`StartAsync` 将节点和代理实例注册进测试 WebApplication，映射正式 `LinkService`。`Headers(peer)` 使用 `TransportProtocol.PeerHeader` 和 `LinkHeader`，携带对端身份与新链路 ID。`ExpectUnavailableAsync` 发起双向流并发送 Hello，只接受预期的 gRPC 错误。`WatcherStub` 仅满足原始 ZooKeeper 客户端接口，不处理事件。

`Headers` 使用 `new()` 目标类型推断和集合初始化器，构造的是 gRPC `Metadata`，不是数据帧里的字段。链路 ID 通过 `services.Ids.GenId()` 新建，用于标识这次连接。

`ExpectUnavailableAsync` 的异常过滤器为：

```csharp
catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
{
}
```

只有指定状态码才算预期拒绝；其他 gRPC 状态、取消或非 gRPC 异常继续传播。如果写入和读取都没有出现预期拒绝，辅助方法主动抛测试失败。这样不能把“发生任何错误”误认为身份检查正确。

## 清理与限制

gRPC 连接、频道、发现枚举和 WebApplication 均有作用域释放；正常路径显式停止应用。外层 `finally` 先释放代理，再删除本次独立根的子节点和根节点，最终关闭原始 ZooKeeper 客户端。删除范围限定为本次检查生成的根，不清理其他进程的发现数据。

`proxy` 虽然已有 `await using`，`finally` 仍显式先调用 `DisposeAsync`，确保它持有的临时身份先消失，再由仍活动的 `owner` 删除剩余测试节点。代理释放是幂等的，之后作用域再次释放不会重新创建资源。最内层 `finally` 保证删除节点失败时仍尝试关闭原始客户端；清理异常不会被统一吞掉。

此文件测试真实身份校验和发布可见性，但没有启动完整 App、多机部署或业务服务。实际是否通过取决于执行结果；源码中的 `PASS` 是全部断言成立时才会输出的文本。

本场景的 Router 没有主动依赖，构造后即可就绪；不要把这个真实网络场景单独理解为已经覆盖“接收方尚未就绪”的所有失败时序。首次门槛与本地就绪检查由开头调用的 [StartupReadinessChecks.cs](StartupReadinessChecks.cs.md) 及其他连接测试补充。
