# RollingLog.cs 代码详解

[源码](../../../../tools/server-console/RollingLog.cs)

## 读什么文件

`GameLog` 给同一进程生命期的日志固定 `<PID>-<UTC 启动 ticks>-` 前缀，再由 Serilog 按小时及文件大小滚动。 `ServerInstance` 为该生命期建立 `RollingLog(directory, prefix)`，每次刷新调用 `ReadAvailable(AddLine)`。传入的 `Action<string>` 表示每解析出一条非空完整行，就调用一次接收函数。读取器只处理字节、文件和行边界，JSON 与状态事件由槽位解释。 [GameLog 源码](../../../../code/server/engine/GameLog.cs) · [ServerInstance 详解](ServerInstance.cs.md)

| 成员 | 本轮读取后如何保留 |
| --- | --- |
| `current` | 可空的元组 `(string Hour, int Part)?`，保存已读到的小时及数字分片。首次读取前没有值 |
| `offset` | 当前文件已读字节数。下次用它设置 `stream.Position` |
| `buffer` | 16 KiB 字节数组，可反复填充 |
| `partial` | 可写内存流，暂存从最近一个换行符以来的字节。跨刷新保留 |
| `budget` | 每次进入方法重新设为 256 KiB，由所有本轮读过的分片共用 |

## `RollKey`、排序和打开文件

1. `ReadAvailable` 检查目录，目录还没创建就返回 `true`。随后只枚举目录内匹配 `prefix + "*.log"` 的文件。
2. `RollKey` 取得不含扩展名的文件名，用 `[prefix.Length..]` 切去固定前缀，再找第一个下划线。若下划线右边能按整数解析，就以左边为小时、整数为分片。找不到或解析失败时，以整个后缀作为小时、分片置 0。比如 `2026112413` 得到 `("2026112413", 0)`，`2026112413_1000` 得到 `("2026112413", 1000)`。
3. LINQ 的 `Select` 为每个路径计算分片键，`OrderBy` 以序号比较排小时，`ThenBy` 同小时按整数排分片。这样 `_999` 先于 `_1000`，两者都早于下一小时开头的文件。已经比 `current` 更早的分片直接跳过。
4. `Open` 用 `FileMode.Open`、`FileAccess.Read` 取得只读句柄。 `FileShare.ReadWrite | FileShare.Delete` 组合共享标志，让服务端还能写入或滚动删除。枚举后文件已消失时 `FileNotFoundException` 转成 `null`，继续下一个。其余异常继续传给上层。
5. 文件发生切换，或当前文件长度小于已保存偏移时，重置 `offset` 及 `partial`。前一种情况避免把两个分片的行拼到一起，后一种从被截断文件的开头重新读。句柄由循环里的 `using var stream` 在离开本轮该文件的作用域时关闭。

## 如何逐段形成一行

每次读取的长度是 `Math.Min(buffer.Length, budget)`，实际可能因文件暂时读到末尾而少一些。当读到正数时，用实际字节数增加 `offset`、扣减 `budget`。循环只搜索 `buffer` 中本次填入的区段，用字节 `'\n'` 定位分隔符。

- 每找到一个换行，先把之前外加这段字节收进 `partial`，再用 UTF-8 一次解码。 `TrimEnd('\r')` 去掉 Windows CRLF 的回车。清空 `partial` 后，把非空行交给 `accept`。
- 一块数据可以包含多条记录，循环用 `start = end + 1` 继续找下一条分隔符。剩下没有换行的尾部保存在 `partial`。
- 零字节表示这一刻已读到当前文件末尾。没有完整换行的部分仍留在 `partial`。如果本轮已用完 256 KiB，立即返回 `false`。否则遍历完可见文件返回 `true`。

例如服务端日志里有 `中文\r\n`，第一轮只读取到“中”的前两个 UTF-8 字节。第一轮将这两个字节留在 `partial`。第二轮读取后续字节和换行，拼齐后再解码，输出 `中文`。若在这两轮之间切到另一分片，读者清理旧分片的残缺尾部。

## 返回值和限制

`true` 表示这次读取已处理当前可见内容，服务端后续仍可能追加。 `false` 表示本轮字节预算耗尽，即使最后读到的字节恰好就是文件尾，也需要下一轮确认。单次读取字节预算有界，但分片枚举、排序和一条迟迟没有换行的记录另有成本。上层 `ServerInstance` 的 1000 行上限只控制可见历史量，长行仍可能使 `partial` 在多轮读取中增长。

重开控制台或切换实际身份时，`ServerInstance` 重新创建读取器，游标从头开始。读取器跨刷新持有数组和内存流，不保存打开的磁盘句柄。可以对照分片排序、半行 UTF-8、截断和积压回放的检查场景。 [日志检查详解](tests/LogChecks.cs.md)
