# LogLevelChecks.cs 代码详解

[源码 LogLevelChecks.cs](../../../../../code/tests/engine/LogLevelChecks.cs)

## 入口与测试准备

引擎检查的 `--logging` 分支调用 `LogLevelChecks.Run()`。方法用 `Path.GetTempPath()` 和随机 GUID 创建本次的临时根目录，样本角色固定为 `new ServerProcess(ProcessType.Login, 1)`。它保存 `Console.Out`、`Console.Error` 和 `EngineEnvironment.LogRoot` 的旧值，因为接下来要把进程的标准流和日志路径临时换掉。`StringWriter` 将终端文本写在内存里，调用 `ToString()` 就能检查已收到的内容。 [检查程序入口](Program.cs.md) · [被测日志实现](../../server/engine/GameLog.cs.md)

## CheckSettings 验证配置怎么绑定最低级别

1. 用 `AppContext.BaseDirectory` 定位测试程序输出目录下的 `config/server.yaml`，经正式的 `ServerConfiguration.Load` 读取，确认测试环境配置显式选择了 `Debug`。
2. `Read<Dictionary<string, object>>()` 取得加载后的配置数据。检查程序用 YamlDotNet 的序列化器生成临时 YAML 文本，去掉样例的 `loggingMinimumLevel: DEBUG` 再用 `File.WriteAllText` 写到临时目录。重新读取，预期缺省值为 `Info`。写临时样本是测试准备动作，生产配置加载器只负责加载和验证数据。
3. 对枚举里的每个值都生成一份临时输入，将大写级别加回 YAML 后检查输出是否等于原枚举值。最后分别写入 `TRACE`、`WARNING`、`999`，要求加载抛 `InvalidDataException` 或 `YamlException`。如果没有异常，测试主动抛出普通 `Exception`。由于 `catch` 只捕获前两种异常，普通 `Exception` 会传播并使检查失败。 [级别声明与配置绑定](../../server/engine/logging/ServerLogLevel.cs.md) · [配置加载](../../server/engine/configuration/ServerConfiguration.cs.md)

## Run 检查过滤、去向和终端镜像

`WriteSamples(log, process)` 按固定顺序写三条日志。Debug 用 `ZK_DISCOVER` 和“发现细节”，Info 用 `LISTEN` 和“开始监听”，Error 用 `PROCESS_FAILED` 和“进程失败”。`Enum.GetValues<ServerLogLevel>()` 枚举所有最低级别，每一轮都创建单独的日志对象。

| 准备和动作 | 预期观测 |
| --- | --- |
| 尚未打开文件。每个级别一轮，将 stdout 改为 `StringWriter`，配置级别并写入三条样本。 | `Debug` 时三条、`Info` 时两条、`Error` 时一条。JSON 的 `instance` 回退为 `Login`。 |
| 尚未打开文件，同时捕获 stdout 和 stderr，以 `bothPrintConsole: true` 写一条错误。 | stderr 恰有一条 Error，stdout 为空。 |
| 为每个级别建独立日志根，调用 `OpenServer(Login1)` 后尝试再打开一次，再写普通样本。 | 第二次打开抛 `InvalidOperationException`。普通样本写文件，此时捕获的 stdout 和 stderr 均为空。 |
| 文件模式下再写一条带引号和换行的错误，打开终端镜像。先释放日志，再读取全部 `.log` 文件。 | 文件里的普通样本按门槛过滤，所有记录的实例为 `Login1`。stderr 恰有一条 Error，文本与文件最后一条完全一致，stdout 仍为空。 |

文件阶段把找到的文件按 `StringComparer.Ordinal` 排序，`SelectMany(File.ReadLines)` 将各文件的行连成一个序列，`ToArray()` 将它读入数组。`lines[..^1]` 拿出最后一条之前的样本，`lines[^1]` 是文件最后那条显式镜像错误。`^` 从末尾计数，`^1` 是最后一个位置。镜像里有原始换行的详情，通过 JSON 转义仍只占一条物理日志行，测试据此逐行解析。

## CheckRows 和 CheckLifecycle 各断言什么

`CheckRows` 接收几条文本和最低级别，用 `switch` 表达式生成预期级别序列。例如最低级别为 `Info`，结果是 `["INFO", "ERROR"]`。它先检查行数，再用 `JsonDocument.Parse` 逐行解析。每行检查级别顺序、`instance`、固定角色 `Login`、当前 PID，并确认 `ev`、`detail` 都有非空文本。`using var row` 在本次循环体结束时释放 JSON 文档。`GetProperty` 要求相应键存在，字段缺失也会令检查失败。 [结构化字段](../../server/engine/logging/LogEventJson.cs.md)

`CheckLifecycle` 创建两份日志，先给旧实例配置 Debug、给当前实例配置 Info，再释放旧实例。`ExpectDisposed(Action)` 将待执行操作当作参数，对旧实例的一次写日志、一次改级别、一次开文件分别预期 `ObjectDisposedException`。重复释放旧实例可以正常返回。最后仅向当前实例写三条样本，预期仍有 Info 和 Error 两条输出，说明旧实例的退出没有污染另一实例的状态。

## 测试如何收尾

外层 `finally` 按保存的旧值恢复 stdout、stderr 和日志根环境变量，再删除临时目录。`try/finally` 的清理在断言抛异常时也执行。该检查会改写当前进程共用的控制台流和环境变量，所以入口顺序执行这些场景。它覆盖小样本的路由和文件逐行格式。比如 `CheckRows` 检查事件与详情非空，文件场景再检查镜像全文相等，此处没有逐个断言全部 JSON 字段或专门制造日志滚动。成功文本位于所有断言和清理之后。
