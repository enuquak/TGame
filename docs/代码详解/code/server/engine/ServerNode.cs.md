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

`ConnectAsync` 先校验实例名称及主动连接方向，再生成新的连接 ID，为目标地址创建 gRPC channel，在元数据中发送本端名称与连接 ID，先写出 Hello，再进入公共 `RunLinkAsync`。channel 与 call 都由此方法的 `using` 管理。获得 `Connect(...)` 返回的 call 不代表应用层 Hello 已完成；`CreateHello()` 只创建 `Frame` 对象，`WriteAsync` 才把消息交给 gRPC 输出流。

`MaintainLinkAsync` 保持针对某一发现记录的连接。正常流结束也被视为断线；对于 I/O、超时、非全局取消，以及 `Unavailable`、`Cancelled`、`Internal`、`AlreadyExists` 这些 gRPC 状态，等待 500 毫秒再重试。其他错误向发现层传播，避免把协议或配置错误永久掩盖为重连。

`AcceptConnectAsync` 校验连接 ID、通过 `AcceptsIncoming` 检查实例名称和入站方向，再查询临时占号并要求本端首次启动已经就绪。主动连接方向是 `configuration.Dials(本端类型, 对端类型)`，入站方向相反；这些静态检查在入口完成，公共握手不再重复检查拓扑。

## RunLinkAsync：握手到断开的完整生命周期

`peer` 是预期的对端实例名，`id` 是本次连接 ID，`input` 和 `output` 始终以本端为视角。主动方把响应流作为 input、请求流作为 output；接收方把请求流作为 input、响应流作为 output。

1. 创建 `PeerLink` 本地对象，此时还没有向 `links` 登记，也没有因此发送消息。
2. `await input.MoveNext(stop)` 读取第一帧，要求 `input.Current.Hello == peer`。流提前结束或 Hello 不匹配会抛 `IOException`；读取本身的异常也向上传播。主动方在入口已经发过 Hello，因此这里等待对端回复；接收方则读取主动方的 Hello。
3. 在 `linkChanges` 锁内检查入站就绪条件，并用 `sessions.Add(peer)` 占位。重复连接返回 `AlreadyExists`。此检查在 `try` 前，拒绝的重复连接不能进入清理块误删原连接。
4. 进入 `try`，接收方回复自己的 Hello。先占位再异步回复，可以防止回复期间另一条同名连接被接纳；锁内没有网络等待。
5. 在锁内加入 `links`、记录 `LINK_UP` 并调用 `TryCompleteFirstLinks`。此后 `CallPeerAsync` 可以找到此连接。
6. 等待 `RunTransportAsync`，直到连接结束。`RunLinkAsync` 返回的 Task 表示整条连接的生命周期，不是“握手成功”的瞬时通知。

`requireReady` 默认为 false，主动方使用默认值；入站使用 true，同时表示“要求本端 Ready”和“需要回复 Hello”。当前 Ready 只会从 false 变成 true，入口检查通过后不会在等待 Hello 时变回未就绪；公共函数仍保留该检查，不能把参数仅理解为动态健康开关。

连接退出时，`finally` 删除会话占位和链接，并关闭 `PeerLink` 以结束所有待回复调用，不撤销首次就绪。[LinkService.cs](LinkService.cs.md) 只提取 gRPC 元数据并转交 `AcceptConnectAsync`。

## CallPeerAsync：本机发起请求

`CallPeerAsync<TReply>` 校验消息非空、调用未取消和目标名称规范，只向已有 `links` 条目发送请求；没有连接时立即返回 `Unavailable`。请求 ID 来自注入的 `IdGenerator`，不会在此临时发现或拨号。

## RunTransportAsync：读流、处理请求与收尾

两端都会同时运行 `ReadAsync` 和 `ServeAsync`，它们是同一连接上的两个异步任务，并非分别专属于主动方和接收方，也没有为每条连接显式创建专用线程。

| 路径 | 接收什么 | 后续动作 |
| --- | --- | --- |
| `ReadAsync` → `ReceiveFrame` | input 中的 Reply | 立即调用 `link.Receive`，按请求 ID 完成本机等待中的 RPC |
| `ReadAsync` → `ReceiveFrame` | input 中的 Request | 写入本地有界请求队列，等待 `ServeAsync` 处理 |
| `ServeAsync` | 本地队列中的 Request | 校验信封，构造相同请求 ID 的 Reply，再调用 `link.WriteAsync` 回复 |
| `CallPeerAsync` → `PeerLink.CallAsync` | 本机要发出的请求 | 登记 `waiting` 后写出 Request，等待对端 Reply；不经过 `ServeAsync` |

这里的 Channel 是进程内请求队列，容量由 `RequestQueueCapacity` 定义为 64。`SingleWriter` 对应 ReadAsync，`SingleReader` 对应 ServeAsync。接收端用 `TryWrite`，满时直接抛 `ResourceExhausted`，不会等待空位；握手后的 Hello、空帧等也会报错。

`ServeAsync` 当前尚无业务处理器，校验请求后直接回复 `Unimplemented`。这是 Reply 内的错误信封，不是立即关闭 gRPC 的异常。同一连接逐个等待回包写完再取下一条请求；与此同时 ReadAsync 仍可读取后续消息，避免等待写出响应时挡住本机 RPC 的响应接收。

收尾顺序对应源码：

1. 创建关联 `stop` 的局部取消源 `transport`。外部取消会传入，内部取消不反向取消外部 stop。
2. 调用两个局部函数，`Task.WhenAny(read, serve)` 得到先结束的任务。先结束包括正常、失败和取消；WhenAny 本身不转抛该子任务的异常。
3. 调用 `transport.Cancel()`、`queue.Writer.TryComplete()`、`link.Close()`，停止另一循环、结束本地队列写入并使未完成 RPC 失败。这不是保证排空队列的优雅关闭。
4. `await Task.WhenAll(read, serve)` 等待两边真正退出并观察异常。
5. 只有出现 `OperationCanceledException` 且外部 stop 未取消时，才改为 `await finished`。例如读取正常 EOF、服务循环被内部收尾取消时，保留读循环的正常完成；先结束任务自身失败或取消仍会传播。外部停止和其他异常不会被这条 catch 吞掉。

局部函数声明写在方法后面，也可以在前面调用；它们捕获外层的 link、input、queue 和 transport。`link.Close()` 关闭本地等待状态，底层 call/channel 的释放仍由外层负责。节点不在单条连接退出时释放共享 ZooKeeper、ID 或日志。

读完后沿 [PeerLink.cs](PeerLink.cs.md) 的 `CallAsync` → `Receive` 跟踪一次请求，特别区分本地请求队列和 `waiting` 字典。
