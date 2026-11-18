# LinkService.cs 代码详解

源码：[code/server/engine/LinkService.cs](../../../../../code/server/engine/LinkService.cs)

## 职责与依赖

`LinkService` 继承生成的 `ServerLink.ServerLinkBase`，是入站双向 gRPC 连接的边界。构造函数接收同一进程的 `ServerNode` 和 `ZooKeeperProxy`，只做接入校验，再将流交给节点；它不创建自己的发现服务或 ID 服务。

## Connect 的检查顺序

1. 从请求元数据读取 `peer` 和 `link`，缺少时按空字符串处理。
2. 用 `IdText.IsValid` 检查连接 ID，用 `node.AcceptsIncoming` 检查规范实例名及拓扑方向。不通过时返回 `InvalidArgument`。
3. 查询 `IsProcessClaimedAsync`，要求对端有有效临时占号。不要求对端已经发布服务地址；否则主动连接方会陷入先就绪才能连接的启动死锁。未占号返回 `Unavailable`。
4. 检查本端 `node.Ready`，入站接收者尚未就绪时返回 `Unavailable`。
5. 调用 `RunLinkAsync(..., requireReady: true)` 进入 hello 握手和流处理。

最后传入 `requireReady: true` 不是重复装饰：节点会在等待首帧之后、持有连接状态锁时再次确认就绪，缩小服务入口检查与正式接纳之间状态变化的窗口。

## 生命周期与边界

所有异步步骤都使用 `ServerCallContext.CancellationToken`，连接取消会传递到 ZooKeeper 查询和流处理。服务不释放注入的共享依赖。这里校验的是“此名称已被某个活动 ZooKeeper 会话占用”和拓扑规则，不应把它描述为带密钥的对端身份认证。

相关详解：[TransportProtocol.cs](TransportProtocol.cs.md)、[ServerNode.cs](ServerNode.cs.md)、[ZooKeeperProxy.cs](ZooKeeperProxy.cs.md)。
