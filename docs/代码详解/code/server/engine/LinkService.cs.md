# LinkService.cs 代码详解

源码：[code/server/engine/LinkService.cs](../../../../../code/server/engine/LinkService.cs)

## 职责与依赖

`LinkService` 继承生成的 `ServerLink.ServerLinkBase`，是入站双向 gRPC 连接的边界。构造函数接收同一进程的 `ServerNode`，提取元数据后将流交给 `AcceptConnectAsync`；它不创建自己的发现服务或 ID 服务。

## Connect 与接收入口的检查顺序

1. `LinkService.Connect` 从请求元数据读取 `peer` 和 `link`，缺少时按空字符串处理，然后转交 `node.AcceptConnectAsync`；下面的检查均由节点完成。
2. 用 `IdText.IsValid` 检查连接 ID，用 `node.AcceptsIncoming` 检查规范实例名及拓扑方向。不通过时返回 `InvalidArgument`。
3. 查询 `IsProcessClaimedAsync`，要求对端有有效临时占号。不要求对端已经发布服务地址；否则主动连接方会陷入先就绪才能连接的启动死锁。未占号返回 `Unavailable`。
4. 检查本端 `node.Ready`，入站接收者尚未就绪时返回 `Unavailable`。
5. 调用 `RunLinkAsync(..., requireReady: true)` 进入 hello 握手和流处理。

`requireReady: true` 表示接收端：公共握手在连接占位前确认首次就绪，并在占位成功后回复本端 Hello。首次就绪不会因依赖断线撤销，因此运行期依赖全部缺失也可接受新的入站连接。

`Connect` 返回的是 `AcceptConnectAsync` 的整条连接任务。它不会在接收到 Hello 后立即结束；后续还要持续运行双向流，直到断开、取消或失败。收到的 Request 由本端 ServeAsync 回复，本端也可以在同一条流上主动发起 RPC。

## 生命周期与边界

所有异步步骤都使用 `ServerCallContext.CancellationToken`，连接取消会传递到 ZooKeeper 查询和流处理。服务不释放注入的共享依赖。这里校验的是“此名称已被某个活动 ZooKeeper 会话占用”和拓扑规则，不应把它描述为带密钥的对端身份认证。

相关详解：[TransportProtocol.cs](TransportProtocol.cs.md)、[ServerNode.cs](ServerNode.cs.md)、[ZooKeeperProxy.cs](ZooKeeperProxy.cs.md)。
