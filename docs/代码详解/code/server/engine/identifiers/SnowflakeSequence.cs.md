# SnowflakeSequence.cs 代码详解

源码：[code/server/engine/identifiers/SnowflakeSequence.cs](../../../../../../code/server/engine/identifiers/SnowflakeSequence.cs)

## 输入和位布局

`SnowflakeSequence` 接收一个固定 worker 编号，并在每次 `TryGenId(timestamp)` 时接收自纪元起的毫秒数。它保留该 worker 的发号进度。时钟读取、加锁、等待和字符串转换由调用它的生成器负责。见 [SnowflakeIdGenerator.cs](SnowflakeIdGenerator.cs.md)。

`EpochMilliseconds` 把 `2026-01-01 00:00:00 UTC` 换算成 Unix 毫秒。外层先用当前 Unix 毫秒减掉它，这个差值才是传进方法的 `timestamp`。

| 从高位到低位 | 位数 | 允许值 |
| --- | --- | --- |
| 符号位 | 1 | 始终为 0 |
| 纪元后的毫秒数 | 41 | `0..2^41-1` |
| worker | 10 | `0..1023` |
| 当前毫秒的序号 | 12 | `0..4095` |

`(1 << WorkerBits) - 1` 先把二进制的 `1` 左移 10 位，再减一，得到低 10 位全为 `1` 的值 `1023`。时间戳掩码使用 `1L << TimestampBits`，后缀 `L` 使左操作数为 64 位的 `long`，这样才能把 `1` 移到第 41 位。

## 构造与状态

构造函数拒绝负 worker 和大于 `MaxWorkerId` 的编号。`workerComponent = (long)workerId << SequenceBits` 把编号放到预定位置，给序号留出低 12 位。`readonly` 表明实例构造完成后保留这一分量。

`lastTimestamp = -1` 表示还没有成功发过 ID。`sequence` 是上次成功发号使用的序号，字段默认值为零，第一条 ID 会在时间前进分支明确把它设为零。

## 逐段读 TryGenId

1. 先检查时间戳是否超出 `0..MaxTimestamp`，再检查它是否小于 `lastTimestamp`。任一条件成立都抛 `InvalidOperationException`，字段仍保留上次成功发号的状态。
2. 时间戳与上一次相同就尝试递增 `sequence`。已经到 `MaxSequence` 时，先返回 `null`，把等待交给外层。时间戳更大时，开始新的一毫秒，将序号归零。
3. 将本次时间存入 `lastTimestamp`，把时间左移 `10 + 12 = 22` 位，再合并三个分量。

```csharp
long timestampComponent = timestamp << (WorkerBits + SequenceBits);
return timestampComponent | workerComponent | (uint)sequence;
```

`|` 是按位或。三个分量各占一块互不重叠的位，所以也可以用普通加法手算结果。序号始终是非负数，`(uint)sequence` 把它作为无符号的低位分量参与拼装。方法返回 `long?`，调用方用空值区分同毫秒的暂时耗尽。

以 worker `42` 为例，`42 << 12 = 172032`。时间戳 `100` 对应 `100 << 22 = 419430400`，首条 ID 为 `419602432`。同毫秒下一条加 `1`，末条加到 `4095`。若此后立即输入 `100`，结果为 `null`。到了 `101`，从 `423796736` 重新开始计数。若输入经历 `100 → 99 → 100`，中间抛异常，最后回到原毫秒时会从已经使用的序号继续。

## 边界

同一 worker 每毫秒最多有 `4096` 个序号。不同 worker 的编号落在中间 10 位，变化时数值也跟着变化。跨进程唯一性还依赖 worker 由 ZooKeeper 独占及其复用时机。见 [ZooKeeperProxy.cs](../ZooKeeperProxy.cs.md)。

时间戳、worker、序号全为零时会合出整数 `0`。这是位布局的边界值，`IdText.IsValid("0")` 会按入站文本规则返回假。接近最大时间戳仍保留符号位，详见 [IdText.cs](IdText.cs.md)、[IdGeneratorChecks.cs](../../../tests/engine/IdGeneratorChecks.cs.md)。
