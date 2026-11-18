# IdGeneratorChecks.cs 代码详解

源码：[code/tests/engine/IdGeneratorChecks.cs](../../../../../code/tests/engine/IdGeneratorChecks.cs)

## 职责与入口分工

此文件分别检查纯本地雪花 ID 规则和真实 ZooKeeper worker 分配。`RunAsync()` 不连接外部服务；`RunZooKeeperAsync(configPath, services)` 是需要显式启用的集成检查，读取 ID 配置并连接 ZooKeeper。两个入口都用异常报告断言失败。

## 本地算法检查

固定 worker `42`、时间戳 `100`，遍历当前毫秒的全部序列槽，要求 ID 唯一且 worker 位正确。用尽槽位后 `TryGenId` 必须返回空值，不能回绕；下一毫秒重新生成的值应超过上一批最大值。随后覆盖时间回退、worker 越界、时间戳越界/负值，以及最大 worker 与最大时间戳仍产生正 `long`。

内部 `ManualClock` 将可修改毫秒数加到雪花纪元上，供 `SnowflakeIdGenerator` 模拟时钟回退。先生成一个 ID、回拨一毫秒并要求抛错，再把时钟调回，要求新 ID 恰为上个 ID 加一，避免回拨异常后序列被错误重置。

真实系统时钟下并行生成 `50000` 个 ID，通过 `ConcurrentDictionary.TryAdd` 捕获重复。释放生成器后继续生成必须抛 `ObjectDisposedException`。本地构造 `ZooKeeperSession` 并注入连接、断开、重连事件，要求首次断开通知宿主且后续重连不能让已失效会话重新可用。最后检查规范十进制正整数 ID：空串、零、前导零、符号、空格、溢出和混合文本均被拒绝。

## ZooKeeper 集成流程

从 ID 配置得到 worker 根路径，以 `-checks/` 和一个测试 ID 隔离本次分配。使用已初始化的 `ZooKeeperProxy` 并发创建四个生成器，要求 worker 不重复，且它们观察的是同一 `proxy.SessionLost` 任务。跨四个 worker 并行生成 `20000` 个 ID，检查跨 worker 唯一性。

释放 worker 为 `0` 的生成器后重新申请，要求复用同一编号且新 ID 不与历史样本重复；单个 worker 释放不能结束共享会话。随后主动释放代理，要求租约失效任务完成，继续生成 ID 抛出异常。

## 依赖、清理与限制

集成分支有独立的 `30` 秒取消源，并另开原始 ZooKeeper 客户端用于删除测试根节点。即使并发创建部分失败，也会收集成功创建的生成器，在 `finally` 逐个释放、删除根节点并关闭原始客户端。清理仍使用同一个超时令牌，因此超时后清理本身也可能失败；这不是无限重试的环境恢复工具。

这些有限样本验证实现约束，不是全分布式场景的数学证明或性能基准。`Check`、`Expect<T>` 只提供本项目控制台检查所需的断言；没有测试框架的自动隔离机制。
