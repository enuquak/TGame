# EngineEnvironment.cs 代码详解

[源码 EngineEnvironment.cs](../../../../../../code/server/engine/configuration/EngineEnvironment.cs)

## 三个环境变量怎样跨进程传递

`EngineEnvironment` 为启动器和 App 约定三把字符串键。环境变量是操作系统进程中的键值对。比如 `EngineEnvironment.Config` 恒等于 `"TGAME_SERVER_CONFIG"`，而这个键在某次启动中可以对应路径 `C:/work/TGame/code/server/config/server.yaml`。

| 成员和键 | 谁使用 | 缺省值和读取规则 |
| --- | --- | --- |
| `Config` → `TGAME_SERVER_CONFIG` | 控制台或脚本选源配置，子进程中由 `ServerConfiguration.SelectedPath()` 读取。 | 启动器未指定时选择仓库默认的 `code/server/config/server.yaml`。直接启动 App 时，如果键缺失、为空或全是空白，`SelectedPath()` 抛出异常。 |
| `RunDirectory` → `TGAME_SERVER_RUN_DIRECTORY` | 启动器先创建本轮目录，App 读取目录用于交换身份和停止文件。 | App 将缺失、空值和空白回退到配置里的 `runDirectory`，转换成绝对路径，再检查目录已存在。 |
| `LogRoot` → `TGAME_SERVER_LOG_ROOT` | `GameLog.OpenServer` 认领实例身份后读取。 | 代码用 `??` 在读取结果为 `null` 时回退到相对当前工作目录的 `logs/server`。 |

## 以控制台启动 Router 为例

控制台在 `ProcessStartInfo.Environment` 为子进程写入三个键，例如将 `Config` 指向源 `server.yaml`，将 `RunDirectory` 指向 `C:/work/TGame/logs/tools/server-console/20261124-080000-0000000`，将 `LogRoot` 指向 `C:/work/TGame/logs/server`。脚本也传递相同的键，通过临时设置当前进程环境让子进程继承，并在 `finally` 恢复原值。 [控制台安排启动参数](../../../../tools/server-console/ConsoleSession.cs.md) · [PowerShell 启停实现](../../../../../../tools/scripts/powershell/Server-ProcessFunctions.ps1)

App 从源配置路径加载 `server.yaml` 及其 include 文件，再读取 `RunDirectory` 决定本轮运行目录。进程认领为 `Router1` 后，身份文件写到运行目录，日志写到 `LogRoot` 下的 `Router1Server`。启动器将可执行文件复制到当轮构建目录，配置仍直接从源路径读取，运行目录也只通过环境变量提供。 [统一配置加载](ServerConfiguration.cs.md) · [App 读环境与写身份](../../app/Program.cs.md) · [日志文件打开流程](../GameLog.cs.md)

## 源码中的静态常量

```csharp
public const string Config = "TGAME_SERVER_CONFIG";
```

`const` 是编译期固定值，`static class` 允许直接写 `EngineEnvironment.Config`，无需构造对象。真正读取当前进程值的表达式是 `Environment.GetEnvironmentVariable(EngineEnvironment.Config)`。路径默认值、解析和存在性检查都位于各自使用点。
