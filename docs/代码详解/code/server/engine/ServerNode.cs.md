# ServerNode.cs 代码详解

源码：[code/server/engine/ServerNode.cs](../../../../../code/server/engine/ServerNode.cs)

## 职责与同类文件

`ServerNode` 是进程连接与基础 RPC 的运行核心。本文件负责身份属性、握手、连接集合、就绪状态、流读写及调用入口；另一部分 [ServerNode.Discovery.cs](ServerNode.Discovery.cs.md) 根据服务发现快照增删主动连接任务。两者是同一个 partial 类型，共享依赖和状态，不是两个节点服务。

构造函数接收不可变 `ServerProcess`、拓扑 `ServerConfiguration`、数量计划 `ServerGroup`、ZooKeeper、ID 和日志。它读取一次 `process.Name` 校验编号，并立即计算初始就绪状态；无主动依赖或所有相关门槛为零的进程可以直接就绪。节点不创建实体，不注册实体归属，也不自行开启监听端口。

## 连接集合与同步

`sessions` 记录已占据连接席位的对端实例，防止同一对端在握手阶段被重复接纳；`links` 则保存已经完成握手、可供 RPC 使用的 `PeerLink`。两者有不同阶段语义。连接接纳、移除及就绪重算放在 `linkChanges` 锁下，`links` 本身使用并发字典以支持调用路径读取。

`Name`、`Role` 从身份对象计算；`linksReady` 是当前健康状态，供读取及状态变化比较。`firstLinks` 是只完成一次的历史信号，`readinessChanges` 是每次状态变更都会替换的等待信号。

## 首次门槛与运行期健康

`HasRequiredLinks` 只检查本进程主动拨号的类型。首次达标前，每种目标类型需要的已握手不同实例数等于 `ServerGroup.GetStartupMinimum`；首次达标后，正门槛缩为 `1`，零门槛保持 `0`。这不会限制实际连接数，也不要求入站连接达到数量。

例如某主动目标类型的首次门槛是 `3`：连接达到三条时 `FirstLinks` 完成、`Ready` 为真；随后只剩一条仍健康，全部断开则 `Ready` 变假；恢复一条即可恢复健康，不会重新等待三条。`FirstLinks` 从不因断线撤销。

`UpdateLinkReadiness` 完成首次信号、比较新旧健康状态并唤醒状态等待者，记录 `LINKS_READY` 或 `LINKS_LOST`。`WaitUntilReadyAsync` 在锁内同时检查状态与取得当前通知任务，醒来后循环复查，支持取消且避免错过状态变化。宿主仍负责在监听与依赖连接完成后发布地址和记录 `PROCESS_READY`。

## 主动建连与入站接纳

`ConnectAsync` 为目标地址创建 gRPC channel，生成新的连接 ID，在元数据中发送本端名称与连接 ID，先写出 hello，再进入公共 `RunLinkAsync`。channel 与 call 都由此方法的 `using` 管理。

`MaintainLinkAsync` 保持针对某一发现记录的连接。正常流结束也被视为断线；对于 I/O、超时、非全局取消，以及 `Unavailable`、`Cancelled`、`Internal`、`AlreadyExists` 这些 gRPC 状态，等待 500 毫秒再重试。其他错误向发现层传播，避免把协议或配置错误永久掩盖为重连。

`AcceptsIncoming` 要求对端名称可解析，且配置中存在“对端类型主动连接本端类型”的有向边。`RunLinkAsync` 则先检查双方是允许的邻居，读取第一帧并要求 hello 与预期实例名一致，再在锁内检查入站就绪条件和重复会话。入站方向会回写本端 hello，之后才把连接加入可调用集合并更新健康状态。

连接退出时，`finally` 删除会话占位和链接，重新计算就绪，并关闭 `PeerLink` 以唤醒所有待回复调用。入站入口额外的临时占号检查见 [LinkService.cs](LinkService.cs.md)。

## 基础 RPC 与双向流

`CallPeerAsync<TReply>` 校验消息非空、调用未取消和目标名称规范，只向已有 `links` 条目发送请求；没有连接时立即返回 `Unavailable`。请求 ID 来自注入的 `IdGenerator`，不会在此临时发现或拨号。

`RunTransportAsync` 将流接收与请求响应拆成两个任务，避免处理请求时阻塞当前流上的回复接收。接收循环遇到 reply 立即交给 `PeerLink.Receive`；遇到 request 放入容量为 64 的有界队列；队列满时抛出 `ResourceExhausted`，握手后的 hello 或空帧也会报错。

服务循环逐个取出请求，验证信封，然后返回带相同请求 ID 的 `Unimplemented` 错误。当前没有业务处理器，不能把这个回复解释为业务成功。

任一循环结束后，公共取消源会取消另一循环，完成队列并关闭待回复调用，再等待两个任务收尾。内部联动取消与外部停止分开处理，保留真正先结束任务的结果。节点只使用共享 ZooKeeper、ID、日志，不在连接退出时释放这些进程级服务。
