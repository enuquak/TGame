# ZooKeeperProxy.cs 代码详解

源码：[code/server/engine/ZooKeeperProxy.cs](../../../../../code/server/engine/ZooKeeperProxy.cs)

## 职责与生命周期

`ZooKeeperProxy` 是进程级 ZooKeeper 服务，统一持有客户端与 [ZooKeeperSession.cs](ZooKeeperSession.cs.md)，提供进程占号、地址发布、服务发现、身份存在性查询和雪花 worker 租约。它实现 `IAsyncDisposable`，由宿主 IoC 管理；构造函数只检查配置并保存共享日志，不连接网络。

`lifecycle` 串行化初始化和释放。`initialized` 表示连接握手已成功，不能用 `zk != null` 代替；`Client` 属性同时拒绝已释放和未初始化访问。`SessionLost` 暴露会话失效任务，`Process` 在检查会话仍活动后返回已认领的身份。

## InitializeAsync：先建立可靠会话

初始化在生命周期锁内创建客户端，等待 `session.Connected`，受调用取消与 `ConnectTimeoutMilliseconds` 共同约束。等待完成后再次检查 token 和会话状态才设置 `initialized`。重复初始化会失败；任何初始化失败都会关闭代理并向上传播异常，不能拿一个已创建但尚未成功连接的客户端继续执行。

## ClaimProcessAsync：空地址临时占号

方法先检查角色枚举、客户端及会话，再用 `Interlocked.Exchange` 保证一个代理只发起一次认领。它逐段创建发现根目录，从编号 `1` 开始尝试创建 `server-类型编号` 的临时节点。节点已存在就试下一个编号；创建成功后保存 `ServerProcess` 并返回。

新节点数据为空，只表示该实例名已被占用，还不能被发现为可连接服务。`TryCreateEphemeralAsync` 只将 `NodeExists` 解释为正常竞争失败，其他错误继续传播。如果申请失败或取消导致创建结果不确定，方法释放整个会话，避免换号重试时留下多个无法确认归属的身份。

## RegisterProcessAsync：最后发布服务地址

发布要求传入身份与本代理认领的身份相等，并通过 `ValidateAddress`。随后更新已存在临时节点的数据，再检查会话仍有效并记录 `ZK_REGISTER`。更新过程失败会关闭会话，防止保留不明确的发布状态。监听、ID 初始化和依赖就绪由宿主先完成，这里不负责整个启动编排。

地址约束很具体：主机必须是 `127.0.0.1`，`Gate` 使用 HTTPS，其他类型使用 HTTP，不允许用户信息、非根路径、查询或 fragment。读取发现地址时会规范化为 URI authority 部分。当前逻辑不是任意公网地址注册中心。

## WatchProcessesAsync：订阅完整快照

发现循环先确保根路径存在，随后对父节点设置 children watch，逐个读取子节点时再设置 data watch。父 watch 捕捉成员增删，data watch 捕捉空地址发布或后续地址变化，两者缺一都会遗漏状态。

`ReadDiscoveredProcessAsync` 只处理 `server-` 前缀：名称非法会记录错误并跳过；数据为空的合法节点只是占号，暂不进入快照；地址非法或非临时节点同样记录错误并跳过。读取期间节点消失视为正常并发变化，下一次父 watch 会使快照重新收敛。

有效记录包含身份、地址和节点 `czxid` 作为 `Generation`。这使连接层能区分同名重启的新节点，详见 [DiscoveredProcess.cs](DiscoveredProcess.cs.md)。单条坏记录不会停止已有健康连接，但网络或会话级故障不会被当作坏记录吞掉。

内部 `Changed` watcher 用容量为 `1`、满时丢弃新写入的 channel 合并事件。消费者关心“需要重新读取”，而不是每个事件的逐条日志；每轮重读时重新安装一次性 watch。整个异步枚举接受取消，产出快照前还会检查会话有效性。

## 入站身份查询

`IsProcessClaimedAsync(name)` 要求规范实例名，查询对应节点是否存在且具有非零 `ephemeralOwner`，查询前后都检查会话。它不要求数据非空，因此刚占号、正在主动连接依赖的进程也可以通过接收端检查。该查询只证明名称被临时会话占用，并不验证调用者持有该节点所有权的凭据。

`stat` 是节点元信息；对这里使用的普通持久节点和临时节点，`getEphemeralOwner()` 为 0 表示持久节点，非零值为临时节点所属的 ZooKeeper 会话 ID。`stat is not null && stat.getEphemeralOwner() != 0` 因而同时检查存在性和临时占号。它不代表该进程已 Ready，也不是连接数；ZooKeeper 会话过期或关闭后清理临时节点，不能把进程刚断网等同于节点立即消失。项目的 ZooKeeperSession 对已建立连接的断开采用失效退出策略，具体见其详解。

## Snowflake worker 租约

`AcquireWorkerAsync` 校验 worker 根路径，建立关联超时，按 `0..1023` 依次尝试临时节点。成功后至少等到申请时刻之后的下一毫秒才交付租约，减少编号立即复用落在同一毫秒的重号风险；这段等待同样受超时和取消约束。全部占满则明确失败。

申请出现异常时关闭共享会话，保证不会遗留归属不确定的 worker 节点。返回的内部 `WorkerLease` 保存编号、共享会话和节点路径，用 `Interlocked.Exchange` 让释放最多执行一次。

`ReleaseWorkerAsync` 在代理已关闭或会话已丢失时无需再删除节点；正常情况在连接超时内删除 worker 节点，删除失败则关闭代理并抛出。宿主释放顺序应保证 ID 生成器先释放 worker，再释放 ZooKeeper 服务。

## 路径辅助与最终关闭

`EnsurePathAsync` 逐段创建持久父节点，只忽略已存在错误；临时身份和 worker 节点使用 `OPEN_ACL_UNSAFE`，访问隔离依赖部署边界，并未在本文件配置 ZooKeeper ACL 身份。

`DisposeAsync` 在生命周期锁内执行 `CloseAsync`。关闭先标记 `disposed` 并使会话失效，再关闭客户端；重复释放直接返回。日志为外部注入共享实例，不在这里释放。会话失效后此代理不能重新初始化，进程应结束并重新启动获取新身份。
