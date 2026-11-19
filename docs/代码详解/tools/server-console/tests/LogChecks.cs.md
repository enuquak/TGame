# LogChecks.cs 代码详解

源码：[LogChecks.cs](../../../../../tools/server-console/tests/LogChecks.cs)

## 测试结构

LogChecks.Run 在独立临时子目录复制 sort.exe 为 LogCheck1Server.exe，并重定向 stdin 维持一个真实子进程。它借助真实路径、PID 和启动时间验证 ServerInstance 与 RollingLog，不启动游戏框架。ProcessName 与 TrafficEvent 是测试专用标识，不新增生产进程类型。

局部 Row 函数生成带 pid、role、instance、time、ev、detail 的 JSON；可覆盖 PID、类型和时间以模拟错误归属。SaveIdentity 写槽位 JSON。序列化使用 UnsafeRelaxedJsonEscaping，确保后续中文测试包含真实多字节 UTF-8。

## 事件和归属

最初日志含启动前时间的失败、错误 PID 的失败、错误类型的失败及正常 LISTEN，另有旧生命期前缀文件。Open 后只允许一条有效记录进入集合，状态必须为初始化中。

追加 LINKS_READY 后仍应初始化中，说明连接就绪不能替代 PROCESS_READY。随后追加 PROCESS_READY 和损坏文本，要求状态就绪、坏文本仍可见。它验证诊断信息可保留，但无效 JSON 不驱动状态变化。

本文件会人工写入 LINKS_LOST 来检查旧事件解析与日志重放。当前引擎运行期断线已不产生该事件，这些断言不表示 ServerNode.Ready 还会随连接丢失变成 false，也不代替真实进程的断线检查。

## 滚动、预算与字符边界

测试写 1200 条大 TRAFFIC 到 _999 分片，再把 LINKS_LOST 写到 _1000。重新 Open 后因为回放预算未追平，必须显示初始化中，不能暂用历史绿色状态。后续多次 Refresh 完成回放后，累计行数应为 1205，内存只保留 MaxRetainedLines，最终状态为连接不全。这同时检测数字分片排序和有界缓存。

下一小时写包含“中文”的 LINKS_READY，但先只写到第一个中文字符的 UTF-8 中间字节。Refresh 不得新增半行或恢复就绪；写完剩余字节和换行后，原文必须完整且状态恢复。随后用更短日志覆盖当前分片，验证文件截断能重置游标并读到 LINKS_LOST。

## 失败优先级和进程所有权

在同一分片追加 PROCESS_FAILED 后再追加 LINKS_READY，进程虽仍活着也必须维持异常退出状态。这防止后续连接事件掩盖明确失败。

测试把身份文件的 ticks 改为另一个时间，新的 ServerInstance 不得附加到同 PID 的当前进程。再恢复正确 ticks，但传入不同 apphost 快照目录，也必须拒绝附加。PID、启动时间、完整镜像路径必须共同匹配。

## 清理和结果

finally 关闭标准输入并最多等 3000 毫秒，未退出则 Kill 后等待。所有断言通过后才打印 PASS；Check 失败时带检查阶段名称抛 InvalidOperationException。它验证本地日志与进程状态边界，不要求真实 Serilog 同时写文件，也不覆盖外部服务连接。
