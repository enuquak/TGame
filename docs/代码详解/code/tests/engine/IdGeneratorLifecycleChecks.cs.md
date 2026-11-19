# IdGeneratorLifecycleChecks.cs 代码详解

源码：[code/tests/engine/IdGeneratorLifecycleChecks.cs](../../../../../code/tests/engine/IdGeneratorLifecycleChecks.cs)

## 检查目标与依赖

`RunAsync()` 验证进程级 `IdGenerator` 包装服务的初始化、取消、隔离、租约监督和释放。它通过工厂注入本地 `SnowflakeIdGenerator`，并直接模拟 `ZooKeeperSession` 事件，不连接真实 ZooKeeper。整个异步检查使用 `5` 秒取消期限。

## 生命周期场景

- 工厂返回失败任务时，初始化前和失败后的 `GenId` 均不能使用，初始化前读取 `LeaseLost` 也必须失败，工厂的 `IOException` 应直接传播。
- 工厂已经创建生成器但同步触发取消时，`InitializeAsync` 必须抛取消异常，并释放这份未正式接管的生成器；包装服务仍不可用。
- 初始化令牌事先已取消时，工厂根本不应被调用，避免无意义的 worker 分配。
- 正常初始化之后再次初始化必须失败；通过 `Parallel.For` 生成 `5000` 个 ID，要求共享实例下不重复。离开 `await using` 后，包装服务和内部生成器都拒绝再生成，重复释放允许完成。
- 新建第二个实例并初始化后，释放旧实例不能影响新实例；新实例的 `LeaseLost` 必须直接反映内部生成器的租约监督任务。
- 模拟会话断开后，等待租约失效并要求生成失败；随后模拟重连也不能复活旧租约。最终释放后，内部和外部实例都必须抛释放异常。

## 断言与清理

`Check` 判断布尔不变量；`Expect<T>` 与 `ExpectAsync<T>` 分别要求同步/异步操作抛出目标异常。匹配异常才算通过，其余异常会继续向外传播。多数对象使用 `await using`，对重复释放的显式调用属于测试内容。

## 限制

这里检查的是服务生命周期与所有权，不验证真实 worker 抢占或租约节点删除。`5000` 个并发样本也不是吞吐量测量；worker 实际分配和跨 worker 唯一性由 `IdGeneratorChecks.RunZooKeeperAsync` 检查。
