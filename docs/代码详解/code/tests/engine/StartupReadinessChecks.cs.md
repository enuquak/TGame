# StartupReadinessChecks.cs 代码详解

源码：[code/tests/engine/StartupReadinessChecks.cs](../../../../../code/tests/engine/StartupReadinessChecks.cs)

## 职责与状态区分

本文件用内存链路检查 `ServerNode` 的首次连接里程碑 `FirstLinks` 与当前健康状态 `Ready`。前者完成一次后保持完成，后者会随依赖连接变化。`RunAsync(stop)` 顺序执行五组场景；全程复用实际连接配置、显式测试依赖，不连接真实 ZooKeeper。

## 五组检查

| 方法 | 构造与关键断言 |
| --- | --- |
| `CheckStartupMinimumAsync` | Base 的 Router 门槛设为 2。Router1、DBMgr1 已握手，Router2 仅启动流时仍不就绪；等待可被取消。Router2 发来 Hello 后首连完成。此后失去一台 Router 仍健康，全部失去只撤销 `Ready`，恢复一台就足够。 |
| `CheckZeroMinimumAsync` | Login 的 Router 门槛为 0，立刻完成首连并就绪；可选 Router 后加入、再离开都不能撤销就绪。 |
| `CheckPublicationWaitAsync` | Gate 的 Login 与 Base 先满足首连，随后 Login 断开。此时新的 `WaitUntilReadyAsync` 必须继续等待，不能因为 `FirstLinks` 历史已完成就返回；等待支持取消，Login 恢复才完成。 |
| `CheckWithoutDependenciesAsync` | Router 不需要主动依赖，直接就绪且 `FirstLinks` 成功，不需要业务初始化或入站连接。此方法也可单独调用。 |
| `CheckDelayedHelloAsync` | Login 先因 Router 链路而就绪，入站 Gate 链路开始后延迟 Hello；期间 Router 断开。Gate 的 Hello 到达时接收端已不就绪，必须以 `Unavailable` 拒绝，不能回发确认 Hello。 |

## 调度与清理

`Open` 统一生成目标类型的 `1` 号实例名、预写 Hello，并调用 `RunLinkAsync`。Router2 场景故意不走该辅助方法，以便控制握手时刻。断线通过完成 `Input.Writer`，并等待链路任务结束后再断言。

需要多条持续连接的场景使用关联取消源，在 `finally` 取消并调用 `StopAsync` 逐个等待任务；`StopAsync` 忽略取消异常，其余错误继续暴露。零门槛场景直接完成流并等待。延迟 Hello 场景把已观察到的预期拒绝任务替换为完成任务，避免清理阶段再次抛出相同故障。

## 覆盖边界

这些测试证明当前状态与历史里程碑的区别，但不会发布 ZooKeeper 地址；方法名中的 publication wait 指发布前应等待的条件。真实地址发布与占号校验另由 `StartupPublicationChecks` 验证。内存连接的执行顺序可控，不构成生产环境的时序压力测试。
