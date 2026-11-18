# LogEventJson.cs 代码详解

源码：[code/server/engine/logging/LogEventJson.cs](../../../../../../code/server/engine/logging/LogEventJson.cs)

## 职责与字段组装

`TGame.Logging.LogEventJson.Serialize` 将一次日志事件转换为 JSON 字符串，不负责选择输出目的地。字段名全部引用 [ServerLogProtocol.cs](ServerLogProtocol.cs.md) 的常量，避免日志生产者和消费者使用不同拼写。

它每次采集当前 UTC 时间和操作系统 PID，将 `ServerLogLevel` 转为大写文本，放入事件名、角色、实例及可选的关联信息。`instance` 参数未提供时使用 `role`：启动早期尚未认领实例仍能生成完整记录，打开实例日志后则由 `GameLog` 传入真实实例名。

## 输出语义

`peer`、`id`、`entity`、`link`、`detail` 没有值时仍以 JSON `null` 出现，字段集合保持一致。使用 `System.Text.Json` 序列化字典，因此引号、换行等由序列化器转义，调用方无需手工拼接 JSON。

字段用途不同：`role` 是类型名，`instance` 是进程实例名，`pid` 是本次操作系统进程；`id` 通常关联请求，`link` 关联一条连接。这里不推导这些关系，也不验证业务含义。

## 使用边界

[GameLog.cs](../GameLog.cs.md) 先决定事件是否达到最低级别，再调用本方法并输出到文件或控制台。本文件没有级别过滤、文件句柄、缓冲队列或释放操作。日志虽然采用 JSON，服务器配置仍是统一 YAML，两者格式职责不同。
