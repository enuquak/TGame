# ServerLogLevel.cs 代码详解

[源码 ServerLogLevel.cs](../../../../../../code/server/engine/logging/ServerLogLevel.cs)

## 从 YAML 到过滤条件

`TGame.ServerLogLevel` 是项目自己的日志级别类型。C# 用 `enum` 声明一组具名整数，每个成员都可以作为这种类型的值，例如 `ServerLogLevel.Info`。本文件省略显式赋值，因此 `Debug`、`Info`、`Error` 的底层整数依次为 0、1、2。源码比较的是映射后的 Serilog 等级，JSON 对外使用下表中的文本。

| 枚举成员 | Serilog 对应值 | JSON 的 `level` | 作为最低级别时保留 |
| --- | --- | --- | --- |
| `Debug` | `LogEventLevel.Debug` | `DEBUG` | Debug、Info、Error |
| `Info` | `LogEventLevel.Information` | `INFO` | Info、Error |
| `Error` | `LogEventLevel.Error` | `ERROR` | Error |

例如把服务器源配置设为 `loggingMinimumLevel: ERROR`，`ServerConfiguration` 绑定到 `ServerLogLevel.Error`，App 再调用 `log.SetMinimumLevel(configuration.LoggingMinimumLevel)`。之后普通的 `LISTEN` Info 日志会被过滤，`PROCESS_FAILED` Error 可以继续输出。仓库的测试环境配置显式选择 `DEBUG`，配置字段缺省时由绑定对象默认取 `Info`。 [统一配置](../configuration/ServerConfiguration.cs.md) · [写入与过滤](../GameLog.cs.md)

## 枚举怎样参与输入校验

枚举在 C# 中能容纳通过转换写入的未定义数值，例如 `(ServerLogLevel)999`。所以 `ServerConfiguration` 绑定后仍用 `Enum.IsDefined(LoggingMinimumLevel)` 检查值，`GameLog.ToSerilogLevel` 也有处理未知值的抛异常分支。日志检查程序覆盖 `TRACE`、`WARNING`、`999` 三种无效 YAML 取值。序列化器用 `level.ToString().ToUpperInvariant()` 生成大写级别文本。 [JSON 序列化](LogEventJson.cs.md) · [配置与日志检查](../../../tests/engine/LogLevelChecks.cs.md)
