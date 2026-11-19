# IdGenerator.cs 代码详解

源码：[code/server/engine/identifiers/IdGenerator.cs](../../../../../../code/server/engine/identifiers/IdGenerator.cs)

## 职责与依赖

`IdGenerator` 是宿主 IoC 注册的进程级 ID 服务，公开 `GenId`、`LeaseLost`、异步初始化和释放。具体雪花序列与 worker 租约由内部 [SnowflakeIdGenerator.cs](SnowflakeIdGenerator.cs.md) 持有，本类型提供明确的生命周期边界。

公开构造函数接收 `IdGeneratorOptions` 和共享 `ZooKeeperProxy`，只构造一个异步创建委托，不立即访问 ZooKeeper。内部委托构造入口便于测试替换创建过程。各消费者通过注入实例调用，不通过静态单例取得服务。

## 初始化流程

`InitializeAsync` 用 `lifecycle` 信号量和释放过程互斥。进入后拒绝已释放对象和重复初始化，再等待创建委托申请 worker 并构造实际生成器。

创建返回后还会再检查取消。如果取消恰好发生在创建完成前后，先释放刚创建的生成器和租约，再抛出取消，避免资源已经申请却没有交给字段持有。只有成功且未取消才通过 `Volatile.Write` 发布 `generator`。

## 调用与停止

`Current` 先拒绝已释放对象，再用 `Volatile.Read` 读取已发布生成器，尚未初始化时明确抛出 `InvalidOperationException`。`GenId()` 与 `LeaseLost` 都通过此入口，不会隐式初始化或生成临时替代 ID。

`DisposeAsync` 与初始化共用生命周期锁，幂等设置 `disposed` 并释放已创建的生成器。它释放自己的 worker 租约，但不自行释放注入的 ZooKeeper 服务。宿主应先停后台任务，再释放 ID 服务，最后关闭 ZooKeeper，会话丢失也会通过 `LeaseLost` 通知宿主停止使用旧身份。
