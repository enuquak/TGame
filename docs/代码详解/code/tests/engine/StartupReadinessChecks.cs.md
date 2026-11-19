# StartupReadinessChecks.cs 代码详解

源码：[code/tests/engine/StartupReadinessChecks.cs](../../../../../code/tests/engine/StartupReadinessChecks.cs)

## 职责与状态区分

本文件用内存链路检查 `ServerNode` 的首次连接里程碑 `FirstLinks` 与启动就绪状态 `Ready`。`Ready` 由首次完成状态派生，两者都不会因运行期依赖断线撤销。`RunAsync(stop)` 顺序执行五组场景；全程复用实际连接配置、显式测试依赖，不连接真实 ZooKeeper。

## 五组检查

| 方法 | 构造与关键断言 |
| --- | --- |
| `CheckStartupMinimumAsync` | Base 的 Router 门槛设为 2。Router1、DBMgr1 已握手，Router2 仅启动流时仍不就绪；等待可被取消。Router2 发来 Hello 后首连完成。此后失去全部 Router 和 DBMgr 仍保持 `Ready`，等待就绪立即返回，恢复连接不重新要求门槛。 |
| `CheckZeroMinimumAsync` | Login 的 Router 门槛为 0，立刻完成首连并就绪；可选 Router 后加入、再离开都不能撤销就绪。 |
| `CheckPublicationWaitAsync` | Gate 的 Login 与 Base 先满足首连，随后两者全部断开。此时 `Ready` 仍为真，新的 `WaitUntilReadyAsync` 必须立即成功，不能重新等待依赖。 |
| `CheckWithoutDependenciesAsync` | Router 不需要主动依赖，直接就绪且 `FirstLinks` 成功，不需要业务初始化或入站连接。此方法也可单独调用。 |
| `CheckDelayedHelloAsync` | 先确认尚未首次就绪的 Login 拒绝入站 Hello；再连接 Router 完成启动，Gate 延迟 Hello 期间 Router 断开，仍须回复 Hello。关闭该 Gate 链路后，所有依赖仍缺失时新建的入站连接也须成功。 |

## 调度与清理

`Open` 统一生成目标类型的 `1` 号实例名、预写 Hello，并调用 `RunLinkAsync`。Router2 场景故意不走该辅助方法，以便控制握手时刻。断线通过完成 `Input.Writer`，并等待链路任务结束后再断言。

需要多条持续连接的场景使用关联取消源，在 `finally` 取消并调用 `StopAsync` 逐个等待任务；`StopAsync` 忽略取消异常，其余错误继续暴露。零门槛场景直接完成流并等待。延迟 Hello 场景先完成并等待旧入站流，再新建同名连接，最后通过关联取消源结束剩余连接。

## 覆盖边界

这些测试证明首次门槛和运行期保持就绪的语义，但不会发布 ZooKeeper 地址；方法名中的 publication wait 指发布前应等待的条件。真实地址发布与占号校验另由 `StartupPublicationChecks` 验证。内存连接的执行顺序可控，不构成生产环境的时序压力测试。
