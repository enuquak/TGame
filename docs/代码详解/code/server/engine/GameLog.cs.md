# GameLog.cs 代码详解

[源码 GameLog.cs](../../../../../code/server/engine/GameLog.cs)

## 一条日志从 App 到文件

App 先创建 `GameLog`，再读取服务器配置，并把配置中的 `LoggingMinimumLevel` 交给 `SetMinimumLevel`。构建容器时注册的就是这个日志对象。ZooKeeper 为进程认领身份后，App 用 `OpenServer(process)` 把后续日志切到实例文件。这样，创建容器或认领身份失败时，App 仍有一个可用于报告错误的日志对象。退出时 App 先结束后台任务、停止监听并释放容器，再由外层 `using var log` 释放日志。 [App 启动流程](../app/Program.cs.md) · [容器注册](../app/ServerServices.cs.md)

例如，启动 `Router` 类型的进程时，配置加载日志先出现在标准输出，JSON 的 `role` 和 `instance` 都是 `Router`。假设认领结果是 `Router1`，此后的监听日志写入 `Router1Server` 下的文件，`role` 仍为 `Router`，`instance` 改为 `Router1`。

## 成员各自保存什么

| 成员 | 用途 |
| --- | --- |
| `FileSizeLimit` | 单个滚动日志文件的大小门槛。单位是字节，`10 * 1024 * 1024` 即 10 MiB。 |
| `changes` | 锁对象。`OpenServer`、`SetMinimumLevel` 和 `Dispose` 用同一把锁串行执行。 |
| `disposed` | 标记该实例是否已经进入释放流程。 |
| `processName` | 打开文件时确定的实例名，用于序列化的 `instance`（启动早期为 `null`）。 |
| `serverLog` | 已创建的 Serilog `Logger`（打开文件之前为 `null`）。 |
| `minimumLevel` | 当前实例独有的 Serilog `LoggingLevelSwitch`。项目的写入路径和文件 logger 共用这个开关。 |

`string?` 和 `Logger?` 在类型声明上表示引用可以为 `null`。`readonly` 限制字段保存的引用可以在哪里赋值，内部对象仍可更新。例如 `SetMinimumLevel` 修改 `minimumLevel.MinimumLevel`，继续使用同一个开关。字段声明里的 `new()` 由左侧类型推断具体构造哪个类。

## OpenServer 如何确定文件

1. `lock (changes)` 获得锁，`ObjectDisposedException.ThrowIf(disposed, this)` 拒绝已释放的实例。进入 `OpenServerCore` 后，已有 `serverLog` 时抛出 `InvalidOperationException`。
2. `Process.GetCurrentProcess()` 提供当前操作系统进程的启动时间，转换为 UTC ticks。`Environment.ProcessId` 提供操作系统 PID。两者共同进入文件路径，区分先后使用同一个 PID 的进程。`using var process` 在本方法离开时释放 `Process` 包装对象。
3. 读取 `EngineEnvironment.LogRoot`。若环境变量返回 `null`，用相对当前工作目录的 `logs/server`。在根目录下创建 `<实例名>Server`，再传给 Serilog 一个类似 `Router1Server/4020-639000000000000000-.log` 的基准路径。实际文件名由 Serilog 填入小时和按大小滚动所需的后缀。 [日志根环境变量](configuration/EngineEnvironment.cs.md)
4. Serilog 按小时滚动，同一小时达到 10 MiB 门槛时允许继续滚动。`retainedFileCountLimit: null` 关闭按保留份数删除历史文件的规则。`outputTemplate: "{Message:lj}{NewLine}"` 把日志消息与换行写入文件。
5. 先给 `processName` 赋值，再用 `Volatile.Write(ref serverLog, logger)` 发布 logger。后续 `Write` 用 `Volatile.Read(ref serverLog)` 读取引用，观察到已发布 logger 的线程也可看到发布前赋好的实例名。`ref` 传递字段所在的存储位置，方便方法直接读写该字段。

`lock` 在退出块时释放锁，包括代码抛出异常的情况。目录创建失败会向调用方传播，App 的外层异常处理负责记录启动失败。

## Write 如何过滤并路由一行 JSON

公开的 `Debug`、`Info`、`Error` 都通过 `=> Write(...)` 转发到同一个方法。`=>` 写在方法参数列表后表示用一个表达式实现方法。可选参数如 `string? detail = null` 能省略，写 `detail: "开始监听"` 可以只传指定的可选参数。

`Write` 先检查释放标记，再调用 `ToSerilogLevel`。这个 `switch` 表达式逐个匹配项目枚举，`Info` 对应 Serilog 的 `Information`。末尾的 `_` 匹配其余值并抛出 `ArgumentOutOfRangeException`。如果映射结果低于当前最低级别，直接返回，跳过序列化。通过过滤后，`LogEventJson.Serialize` 把事件、关联字段和实例名组装成一行 JSON。 [日志级别](logging/ServerLogLevel.cs.md) · [JSON 字段](logging/LogEventJson.cs.md)

| 文件 logger | `bothPrintConsole` | 通过过滤后的去向 |
| --- | --- | --- |
| 尚未打开 | `false` | 标准输出 `Console.Out` |
| 尚未打开 | `true` | 标准错误 `Console.Error`，只写一次 |
| 已打开 | `false` | 文件 |
| 已打开 | `true` | 文件与标准错误，使用同一个 JSON 字符串 |

文件分支使用 `logger.Write(severity, "{Json:l}", json)`，其中 `:l` 让 JSON 字符串按原文呈现，文件模板再加行尾。公开的 `Error` 提供 `bothPrintConsole` 参数。App 用它报告失败，让文件打开前后的错误都能在标准错误看到。这个分支仍受最低级别检查控制。`SetMinimumLevel` 可以在打开文件前后调用，文件 logger 也通过 `ControlledBy(minimumLevel)` 读取同一个级别开关。

## 释放行为与并发边界

`Dispose()` 在锁内检查 `disposed`，首次调用将其设为 `true`，然后用 `serverLog?.Dispose()` 释放现有 logger。`?.` 只在引用有值时调用。后续再次 `Dispose()` 直接返回，再次写入、开文件或设置级别会触发已释放检查。

写入路径没有获取 `changes`，因此读取状态、序列化和输出并非一个持锁操作。`Volatile.Read/Write` 解决的是文件 logger 的发布和可见性，调用方仍须在释放前结束异步写日志的工作。App 的停止顺序兼顾这一点。`LogLevelChecks` 用独立日志对象确认一个实例的释放和级别调整只影响该实例。 [日志检查](../../tests/engine/LogLevelChecks.cs.md)
