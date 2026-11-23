# IdGeneratorChecks.cs 代码详解

源码：[code/tests/engine/IdGeneratorChecks.cs](../../../../../code/tests/engine/IdGeneratorChecks.cs)

## 两个入口的准备条件

测试程序的默认模式和 `--engine` 会走 `RunAsync()`，使用手动时钟、系统时钟和手工注入的会话事件。`--id-zookeeper <配置路径>` 走 `RunZooKeeperAsync(configPath, services)`，需要实际可连接的 ZooKeeper。两个入口分别让位运算和租约行为可以独立检查。见 [测试入口](Program.cs.md)、[TestServices.cs](TestServices.cs.md)。

## RunAsync 的算法和时钟场景

1. **固定毫秒内的位布局。** 构造 worker `42` 的 `SnowflakeSequence`，把时间戳固定为 `100`。循环从 `0` 到 `MaxSequence`，共检查 `4096` 次。`TryGenId(Timestamp)!.Value` 的 `!` 抑制编译器的可空提示，随后 `.Value` 仍要求实际有值。`HashSet<long>.Add` 第一次添加某个 ID 才返回真。表达式 `id >> SequenceBits & MaxWorkerId` 先右移丢掉序号，再取低 10 位，要求提取到原 worker `42`。见 [SnowflakeSequence.cs](../../server/engine/identifiers/SnowflakeSequence.cs.md)。
2. **本毫秒用尽和越界。** 第 `4097` 次传入 `100` 要返回 `null`；随后传入 `101` 要产生大于上一毫秒最大值的 ID。用过 `101` 再输入 `100` 必须报回拨。测试还用负 worker、超上限 worker、负时间戳和超上限时间戳检查拒绝路径，并组合最大 worker 与最大时间戳，断言结果仍然为正。
3. **回拨期间状态保留。** `ManualClock(long timestamp) : TimeProvider` 用主构造函数参数初始化可修改的 `Timestamp`。它的 `GetUtcNow()` 把相对毫秒加到项目纪元上，生产生成器换算时再减回去。先生成一个 ID，把时钟减 `1` 并要求抛错，再把时钟拨回原毫秒。下一条必须恰好是首条加 `1`，证明回拨失败没有把序号重置成 `0`。
4. **并发、释放和会话失效。** 以系统时钟构造本地生成器，用 `Parallel.For` 生成 `50000` 个 ID。所有线程写入同一 `ConcurrentDictionary<string, byte>`，只利用键的唯一性，值统一为 `0`。`TryAdd` 为假即表示重复。释放后再发号应抛 `ObjectDisposedException`。另外手工发送已连接、断开、再次连接事件，检查断开时失效任务完成，随后仍无法通过活动检查。
5. **文本边界。** 空串、零、前导零、符号、前后空格、正向溢出和混合文本都应返回假；用固定时钟生成的实际文本应返回真。见 [IdText.cs](../../server/engine/identifiers/IdText.cs.md)。

`Check(condition, message)` 在断言为假时抛出带场景说明的异常。`Expect<T>(Action action) where T : Exception` 通过泛型约束要求 `T` 是异常类型，捕获匹配类型时结束；操作没有抛出匹配异常时，断言不会通过。可控的时钟使回拨检查无需依赖机器实际调整时间。

## RunZooKeeperAsync 如何检查租约

方法从参数指定的完整配置读取 worker 根。实际 ZooKeeper 的连接选项由 `ZooKeeperProxyChecks.LoadOptions()` 从测试程序当前选中的配置读取。两者分别提供根路径和连接目标。用 `<worker 根>-checks/<测试 ID>` 隔离本轮分配。这里的测试 ID 来自 `services.Ids.GenId()`。代理负责创建 worker，另起一个原始客户端用于清理持久的测试根。`30` 秒令牌约束传入它的等待。

`Enumerable.Range(0, 4).Select(...).ToArray()` 创建四个申请任务。`await Task.WhenAll(tasks)` 等待这批任务完成，内部 `finally` 收集所有 `IsCompletedSuccessfully` 的任务结果，便于其中一个失败时仍有引用可释放。检查要求四个 worker 互不相同，并用 `ReferenceEquals` 确认四个生成器的 `LeaseLost` 正是代理的同一个任务。接着用 `i % generators.Count` 选生成器，并行生成 `20000` 个 ID 检查重复。

测试找到 worker `0` 并释放，再申请一次，要求得到 `0` 且新 ID 可以加入前面的唯一性集合。此时 `proxy.SessionLost` 应仍未完成。显式关闭代理后，新生成器的租约失效任务应完成，调用 `GenId` 则抛 `InvalidOperationException`。这是单个 worker 的释放和整个会话关闭的两种结果。见 [ZooKeeperProxy.cs](../../server/engine/ZooKeeperProxy.cs.md)、[SnowflakeIdGenerator.cs](../../server/engine/identifiers/SnowflakeIdGenerator.cs.md)。

## 清理与检查范围

外层 `finally` 尝试逐个释放已取得的生成器，有测试根时删除它，然后关闭原始客户端。根节点检查和删除仍使用原来令牌，因此取消也会影响清理。并发分配在同一代理会话的独立根下完成，样本用于检查重复和一次 worker 复用，未测量吞吐量或比对独立 ZooKeeper 会话间的系统时钟。
