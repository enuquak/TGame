# RollingLog.cs 代码详解

源码：[RollingLog.cs](../../../../tools/server-console/RollingLog.cs)

## 职责和游标

RollingLog 增量读取某个进程生命期的 Serilog 文件分片。构造参数 directory 是实例日志目录，prefix 包含 PID 和启动时间；因此文件枚举不会把同名实例的其他运行历史混进来。

current 保存当前小时及数字分片编号，offset 保存字节偏移。partial 是跨读取保留的不完整行字节，buffer 为 16 KiB。每次 ReadAvailable 最多读 256 KiB，以限制 WPF 单次刷新花在历史回放上的时间。返回 true 表示当前可见分片已读尽；false 表示达到预算，需要下一次刷新继续。

## 按时间和分片读取

ReadAvailable 先处理日志目录尚未创建的情况，此时返回 true。之后枚举 prefix*.log，RollKey 从文件名去掉前缀，再提取小时和下划线后的数字。排序先按小时字符串再按整数分片号，因此 _999 排在 _1000 前，不使用错误的字典序。

比 current 更早的分片跳过。Open 使用 FileShare.ReadWrite | FileShare.Delete，允许服务端追加、滚动和删除文件；如果枚举后文件已被删除，FileNotFoundException 转为 null 并继续。其他 I/O 异常由上层 ServerInstance 决定是否重试。

切换分片或发现文件长度小于 offset 时，游标回到 0，并清空 partial。后一种情况覆盖日志文件被截断的情形；前一分片末尾未完成的半行不会与下一文件拼接。

## 字节边界与完整行

读取循环先更新 offset 和预算，再查找换行字节。换行之前的字节加入 partial 后一次性按 UTF-8 解码，去掉行尾回车，并把非空行交给 accept。剩余未换行的字节继续保存在 partial 中。

这种顺序允许一个中文字符跨多次文件写入：只有完整一行到达后才解码，不会因为本轮只读到 UTF-8 前半个字符而生成替代字符。若本次恰好消耗完预算，返回 false，即便剩余内容很少；下一次读取会确认追平。

## 上下游和生命周期

ServerInstance 持有本读取器，并在启动生命期变化或服务器实际身份确定后重新创建。MainWindow 不直接操作文件，只消费 ServerInstance 的有界行集合和累计行数。RollingLog 不保持打开的 FileStream，每个分片在本次枚举结束时由 using 释放；跨 tick 保存的是游标和未完成行。

日志检查覆盖数字分片排序、跨小时、半行中文、截断与大积压重放。这里的预算限制单次读取工作量，而 ServerInstance 的 1000 行上限限制展示历史量，两者解决不同问题。
