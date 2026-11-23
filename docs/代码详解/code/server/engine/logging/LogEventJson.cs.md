# LogEventJson.cs 代码详解

[源码 LogEventJson.cs](../../../../../../code/server/engine/logging/LogEventJson.cs)

## 从方法参数到 JSON

`LogEventJson.Serialize` 接收日志级别 `level`、事件名 `ev`、角色 `role`，以及可选的 `peer`、`id`、`entity`、`link`、`detail`、`instance`。每次调用建立 `Dictionary<string, object?>` 并交给 `System.Text.Json.JsonSerializer.Serialize` 返回字符串。字典键都是字符串，`object?` 使同一字典可以装入时间、整数、字符串和 `null`。

```csharp
[ServerLogFields.Time] = DateTimeOffset.UtcNow,
[ServerLogFields.Instance] = instance ?? role,
[ServerLogFields.Pid] = Environment.ProcessId,
[ServerLogFields.Peer] = peer,
```

这是字典的初始化写法，形如 `[键] = 值`。`??` 在左侧为 `null` 时取右侧，例如启动早期 `instance` 没传，就先填 `role`。传入空字符串时左侧仍有值，代码会保留空字符串。时间由每次调用采集，带 UTC 偏移信息。PID 来自当前操作系统进程，JSON 里是数值。`level.ToString().ToUpperInvariant()` 把枚举名称转成固定的大写拼写。

## 一条示例记录

假设 `GameLog` 已用 `Login1` 的身份打开日志文件，然后以 Info 级别写监听事件，将地址放进 `detail`。下面用示例时间和 PID 展示该调用生成的数据。

```json
{"time":"2026-11-24T08:00:00+00:00","level":"INFO","ev":"LISTEN","role":"Login","instance":"Login1","pid":4020,"peer":null,"id":null,"entity":null,"link":null,"detail":"http://127.0.0.1:5001"}
```

可选参数没传时，其键仍在字典中，序列化结果保留 JSON `null`。关联字段有值时也按原样写入，例如 `UNMATCHED_REPLY` 会把对端、请求 ID、连接 ID 分别填入 `peer`、`id`、`link`。字符串里的引号和换行由序列化器转义，序列化结果中的 `\n` 是两个字符，读回 JSON 后恢复为一个换行。这样一条多行异常详情仍只占日志文件中的一条物理行。 [字段与事件约定](ServerLogProtocol.cs.md)

## 调用方和读者

`GameLog.Write` 先按级别决定是否调用本方法，再将返回的字符串传给文件或终端。标准错误镜像复用同一个字符串，因此一条镜像记录的时间、PID 和正文与文件中的记录匹配。控制台读到 JSON 后，用 `role`、`instance`、`pid`、`time` 核对记录归属，用 `ev` 更新进程状态。展示层可以把 UTC 时间换算成本地时间。 [写入路径](../GameLog.cs.md) · [控制台归属检查](../../../../tools/server-console/ServerInstance.cs.md) · [日志展示](../../../../tools/server-console/LogEntry.cs.md)
