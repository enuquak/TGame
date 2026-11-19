# SnowflakeIdGenerator.cs 代码详解

源码：[code/server/engine/identifiers/SnowflakeIdGenerator.cs](../../../../../../code/server/engine/identifiers/SnowflakeIdGenerator.cs)

## 职责与持有资源

内部 `SnowflakeIdGenerator` 将时钟、[SnowflakeSequence.cs](SnowflakeSequence.cs.md) 和 ZooKeeper worker 租约组合成字符串 ID 生成器。`WorkerId` 表示本实例独占的编号，`LeaseLost` 将租约依赖的会话失效任务向外暴露。

内部构造函数允许传入 worker、`TimeProvider` 和可选会话，方便验证时钟及序列边界。没有会话时创建一个不会主动完成的失效任务。正常运行使用私有租约构造函数，绑定系统时钟和真实 `WorkerLease`。

## CreateAsync 与发号流程

`CreateAsync` 要求配置非空，通过注入的 ZooKeeper 代理在 `WorkerRootPath` 申请 worker，再构造生成器；它不会自己创建第二个 ZooKeeper 连接。

`GenId` 在 `sync` 锁中检查是否已释放、会话是否仍活动，然后读取 UTC 毫秒数并减去雪花纪元，交给 `TryGenId`。获得数值后使用 `InvariantCulture` 转成十进制字符串返回。锁让多个线程共享同一份时间戳与序列状态时不会并发发出相同组合。

如果同一毫秒的序列耗尽，`TryGenId` 返回 `null`；生成器退出锁，阻塞当前线程睡眠 1 毫秒后重新尝试。等待在锁外执行，使其他调用和释放有机会进入，但这仍是同步方法，不是可取消的异步等待。

## 错误与释放

时钟回拨、超出纪元范围、会话失效或已释放都会直接抛出，不用随机 ID 或旧时间掩盖错误。特别是会话失效后不能继续使用曾经租到的 worker 编号。

`DisposeAsync` 在同一锁内幂等标记已释放，再在锁外异步释放 worker 节点，避免持锁执行网络 I/O。它不关闭共享 ZooKeeper 客户端；本对象由 [IdGenerator.cs](IdGenerator.cs.md) 持有并在进程清理阶段释放。
