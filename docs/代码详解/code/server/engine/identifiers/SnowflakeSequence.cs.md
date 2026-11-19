# SnowflakeSequence.cs 代码详解

源码：[code/server/engine/identifiers/SnowflakeSequence.cs](../../../../../../code/server/engine/identifiers/SnowflakeSequence.cs)

## 职责与位布局

`SnowflakeSequence` 只负责把相对毫秒时间戳、worker 编号及毫秒内序号组合为整数，不读系统时钟、不访问 ZooKeeper，也不主动等待。纪元固定为 UTC `2026-01-01 00:00:00`。

| 部分 | 位数 | 合法范围 |
| --- | --- | --- |
| 相对毫秒时间戳 | 41 | `0..(2^41-1)` |
| worker 编号 | 10 | `0..1023` |
| 同毫秒序号 | 12 | `0..4095` |

总计占用 63 位，保留 `long` 的符号位。构造函数检查 worker 范围，并预先计算 `workerId << 12`，后续发号只复用这个固定分量。

## TryGenId 的状态转换

`lastTimestamp` 初值为 `-1`。每次输入先检查纪元范围，再拒绝比上次更小的时间戳。时间前进时序号归零；时间相同时序号递增。若同一毫秒已经使用序号 `4095`，返回 `null`，不修改最后时间和序号，交给上层等待下一毫秒。

有效结果按 `(timestamp << 22) | workerComponent | sequence` 合成。这样同一 worker 在同一毫秒最多发出 4096 个不同值，不同 worker 占据独立的编号区间。

## 错误与边界

回拨错误会立即抛出，状态保留在上次成功时间；后续时钟追平后才可能继续。超出 41 位时间范围也会抛出，而不是截断位数。这个类本身不加锁，由 [SnowflakeIdGenerator.cs](SnowflakeIdGenerator.cs.md) 在外围串行调用。

位拼装本身允许纪元零毫秒、worker `0`、序号 `0` 得到整数 `0`；它负责数值算法，文本正数规则由 [IdText.cs](IdText.cs.md) 规定。实际系统以当前时钟运行时已处于纪元之后。worker 唯一性与编号复用等待由 ZooKeeper 租约层保证，不由本序列类证明。
