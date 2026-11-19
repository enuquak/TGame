# 进程服务 IoC 设计

用户已接受会话中的清单并要求实施：将 ZK、ID、日志、节点和配置改为依赖注入，保留现有进程基础框架行为。

## 范围与方案

采用 .NET 内置容器，App 负责注册和启动，Engine 构造函数接收具体依赖，不引入通用服务定位器、第三方容器或无必要接口。相较继续使用静态全局入口，这能明确依赖并隔离测试；仅将现有实例注册、保留静态调用不能实现此目标。

- ZooKeeperProxy(ZooKeeperOptions options, GameLog log)：构造不连接，实例 InitializeAsync(CancellationToken stop = default) 建立会话；ClaimProcessAsync 保持显式调用，Process 返回认领完成的身份。实例操作和 DisposeAsync 不影响其他实例。
- IdGenerator(IdGeneratorOptions options, ZooKeeperProxy zooKeeper)：实例 InitializeAsync(stop)、GenId、LeaseLost、DisposeAsync；内部测试构造接受 Func<CancellationToken, Task<SnowflakeIdGenerator>>。Snowflake 实现仍由它持有。
- GameLog：公开构造函数，实例方法保留现有参数和日志协议。App 在配置加载前创建，并作为已有实例注册，App 显式最后释放；这是保证启动失败日志的生命周期安排。
- ServerConfiguration.Load(path) 返回独立只读配置，提供 DialTargets 属性及 GetDialTargets(type)、Dials、ArePeers 实例查询；删除静态 Topology 和配置释放哨兵。
- ServerSettings.LoadSelected() 显式加载选定配置；删除 Lazy Current。宿主配置不能进入 Engine。
- ServerNode(ServerProcess process, ServerConfiguration configuration, ZooKeeperProxy zooKeeper, IdGenerator ids, GameLog log)；PeerLink 的现有构造参数后追加 GameLog log；LinkService 注入 ZooKeeperProxy。
- ServerProcess 在认领完成后通过容器工厂返回同一身份，解析节点前必须先完成认领。ZooKeeperOptions、IdGeneratorOptions 在 App 加载校验后注入。
- 已有 Gate 证书单例继续使用。ProfileStore 等未接入启动的工具仅改显式参数，消除被删除的静态入口，不恢复业务运行。

## 生命周期与边界

日志 → 配置校验 → 构建一个根容器 → ZK 连接与认领 → 打开进程日志 → ID 初始化 → 监听及连接 → 发布地址。退出时先取消并等待后台工作，停止监听，再异步释放容器（ID 先于 ZK），最后释放日志。启动任一步失败都释放已创建资源并记录错误。

不更改协议、事件格式、就绪语义、证书来源、进程命名及 SinglePoint 移除范围。纯工具和连接实例不注册为进程单例。单例同步与初始化校验继续保留。

## 验证

仅执行本地短检查：构建 App/EngineTests；IoC 共享实例、容器隔离、释放、初始化失败；日志与配置；ID 生命周期；基础 RPC/就绪；必要的本地 gRPC 发现。真实 ZooKeeper、数据库和完整进程组测试不运行。
