# 进程服务方法整理

用户已批准 ZooKeeperProxy 的三项整理，并要求检查其他单例的类似重复逻辑。范围限定为当前进程基础框架，不新增业务或通用抽象框架。

- ZooKeeperProxy：worker 根目录创建复用 EnsurePathAsync；进程/worker 抢占共用 TryCreateEphemeralAsync（仅 NodeExists 返回 false，其余异常继续传播）；发现循环把单条进程解析、读数据、校验及忽略无效记录交给 ReadDiscoveredProcessAsync。
- 检查 ID、日志、节点及配置：只抽取有明确职责边界或实际重复的步骤；不引入生命周期基类、泛型执行器或接口。
- ServerNode 优先把握手/连接生命周期与双向收发拆开，并按既有派生属性规范直接使用 ServerProcess 和连接配置。
- ServerConfiguration 将无环校验集中到 ValidateAcyclic；App 将本机身份文件的临时写入和替换集中到 WriteProcessIdentity，返回现有停止监控所需的 identityKey。
- 保留日志协议、公开服务 API、就绪语义、锁范围、先后顺序、取消/超时及资源释放行为。进程编号从 1 开始，worker 从 0 开始且有上限，复用等待不合并进通用编号算法。
- 只运行必要的短范围本地检查；实际 ZooKeeper/数据库与完整进程组不运行。纯提取复用现有行为回归，不增加仅验证私有实现结构的测试。
