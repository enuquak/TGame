# ServerNode.cs 代码详解

源码：[code/server/engine/ServerNode.cs](../../../../../code/server/engine/ServerNode.cs)

## 职责与同类文件

`ServerNode` 是进程连接与基础 RPC 的运行核心。本文件负责身份属性、握手、连接集合、就绪状态、流读写及调用入口；另一部分 [ServerNode.Discovery.cs](ServerNode.Discovery.cs.md) 根据服务发现快照增删主动连接任务。两者是同一个 partial 类型，共享依赖和状态，不是两个节点服务。

构造函数接收不可变 `ServerProcess`、统一配置 `ServerConfiguration`、该配置持有的数量计划 `ServerGroup`、ZooKeeper、ID 和日志。它读取一次 `process.Name` 校验编号，并立即计算初始就绪状态；无主动依赖或所有相关门槛为零的进程可以直接就绪。节点不创建实体，不注册实体归属，也不自行开启监听端口。

## 连接集合与同步

`sessions` 记录已占据连接席位的对端实例，防止同一对端在握手阶段被重复接纳；`links` 则保存已经完成握手、可供 RPC 使用的 `PeerLink`。两者有不同阶段语义。连接接纳、移除及首次门槛检查放在 `linkChanges` 锁下，`links` 本身使用并发字典以支持调用路径读取。

`Name`、`Role` 从身份对象计算；`firstLinks` 是首次连接达标后只完成一次的信号。`Ready` 直接读取其 `IsCompletedSuccessfully`，不另存就绪布尔值或运行期状态变化通知。

## 首次门槛与运行期就绪

`TryCompleteFirstLinks` 合并首次门槛检查和完成信号：已经 `Ready` 就直接返回，否则检查本进程每种主动目标类型的已握手不同实例数是否达到 `ServerGroup.GetStartupMinimum`。全部满足后完成 `firstLinks` 并记录一次 `LINKS_READY`。实际连接数不受门槛限制，也不要求入站连接达到数量。

例如某主动目标类型的首次门槛是 `3`：连接达到三条时 `FirstLinks` 完成、`Ready` 为真；之后即使全部断开也保持就绪，继续接收入站并发现、重连。恢复的连接可以立即用于通信，不会重新等待三条；缺失对端的 RPC 仍返回 `Unavailable`。

`TryCompleteFirstLinks` 只在构造及加入握手连接时调用；断线不再重算就绪，也不再产生运行期 `LINKS_LOST`。`WaitUntilReadyAsync` 检查取消并等待同一个首次完成任务；已经完成时立即返回。宿主仍负责在监听与首次依赖连接完成后发布地址和记录 `PROCESS_READY`。

## 主动建连与入站接纳

`ConnectAsync` 先校验实例名称及主动连接方向，再为目标地址创建 gRPC channel，生成新的连接 ID，在元数据中发送本端名称与连接 ID，先写出 hello，再进入公共 `RunLinkAsync`。channel 与 call 都由此方法的 `using` 管理。

`MaintainLinkAsync` 保持针对某一发现记录的连接。正常流结束也被视为断线；对于 I/O、超时、非全局取消，以及 `Unavailable`、`Cancelled`、`Internal`、`AlreadyExists` 这些 gRPC 状态，等待 500 毫秒再重试。其他错误向发现层传播，避免把协议或配置错误永久掩盖为重连。

`AcceptConnectAsync` 校验连接 ID、通过 `AcceptsIncoming` 检查实例名称和入站方向，再查询临时占号并要求本端首次启动已经就绪。`RunLinkAsync` 读取第一帧并要求 hello 与预期实例名一致，在锁内检查入站就绪条件和重复会话；接收端回复 hello 后才把连接加入可调用集合并尝试完成首次门槛。

连接退出时，`finally` 删除会话占位和链接，并关闭 `PeerLink` 以结束所有待回复调用，不撤销首次就绪。[LinkService.cs](LinkService.cs.md) 只提取 gRPC 元数据并转交 `AcceptConnectAsync`。

## 基础 RPC 与双向流

`CallPeerAsync<TReply>` 校验消息非空、调用未取消和目标名称规范，只向已有 `links` 条目发送请求；没有连接时立即返回 `Unavailable`。请求 ID 来自注入的 `IdGenerator`，不会在此临时发现或拨号。

`RunTransportAsync` 将流接收与请求响应拆成两个任务，避免处理请求时阻塞当前流上的回复接收。接收循环遇到 reply 立即交给 `PeerLink.Receive`；遇到 request 放入容量为 64 的有界队列；队列满时抛出 `ResourceExhausted`，握手后的 hello 或空帧也会报错。

服务循环逐个取出请求，验证信封，然后返回带相同请求 ID 的 `Unimplemented` 错误。当前没有业务处理器，不能把这个回复解释为业务成功。

任一循环结束后，公共取消源会取消另一循环，完成队列并关闭待回复调用，再等待两个任务收尾。内部联动取消与外部停止分开处理，保留真正先结束任务的结果。节点只使用共享 ZooKeeper、ID、日志，不在连接退出时释放这些进程级服务。
