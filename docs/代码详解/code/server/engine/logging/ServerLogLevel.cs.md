# ServerLogLevel.cs 代码详解

源码：[code/server/engine/logging/ServerLogLevel.cs](../../../../../../code/server/engine/logging/ServerLogLevel.cs)

## 职责与取值

`TGame.ServerLogLevel` 定义项目公开的三个日志级别：`Debug`、`Info`、`Error`。它让宿主配置、日志调用和序列化使用同一种类型，不把 Serilog 的全部级别直接暴露到项目接口。

## 数据流与边界

[GameLog.cs](../GameLog.cs.md) 将它们分别映射为 Serilog 的 Debug、Information、Error，并据此控制最低输出级别。[LogEventJson.cs](LogEventJson.cs.md) 使用名称的大写形式生成 `DEBUG`、`INFO`、`ERROR` 字段。

枚举没有显式数值，顺序从零开始，但实际级别比较在转换为 Serilog 级别后进行。增加枚举成员时必须同步补齐 `GameLog.ToSerilogLevel`；遗漏会抛出未知级别异常，不会默认当作普通信息。本文件不保存当前最低级别，也不产生任何日志 I/O。
