# EngineEnvironment.cs 代码详解

源码：[code/server/engine/configuration/EngineEnvironment.cs](../../../../../../code/server/engine/configuration/EngineEnvironment.cs)

这份文件集中声明三个环境变量的名字。它们把启动器选择的路径交给子进程；本文件只保存键名，不读取环境、不保存路径值，也不修改配置文件。

## 1. 键名和变量值要分开

```csharp
namespace TGame;

public static class EngineEnvironment
{
    public const string Config = "TGAME_SERVER_CONFIG";
    public const string RunDirectory = "TGAME_SERVER_RUN_DIRECTORY";
    public const string LogRoot = "TGAME_SERVER_LOG_ROOT";
}
```

`static class` 是不需要实例的常量容器。`public const string` 表示外部调用方可以使用的固定字符串。

例如 `EngineEnvironment.Config` 的值始终是 `"TGAME_SERVER_CONFIG"`，不是 `server.yaml` 的路径。真正的路径要再从操作系统环境取得：

```csharp
var path = System.Environment.GetEnvironmentVariable(EngineEnvironment.Config);
```

这里 `path` 可能为 `null`，表示该变量不存在。环境变量的值是当前进程中的文本，不等于一个持续共享、自动同步的配置对象。

## 2. Config：选定源配置文件

[ServerConfiguration.cs](ServerConfiguration.cs.md) 的 `SelectedPath` 使用：

```csharp
var path = System.Environment.GetEnvironmentVariable(EngineEnvironment.Config);
if (string.IsNullOrWhiteSpace(path))
{
    throw new InvalidOperationException($"Select a server configuration with {EngineEnvironment.Config}.");
}

return path;
```

`IsNullOrWhiteSpace` 同时拒绝不存在、空串和全空白。缺少值时，服务器入口明确报错，不在这里暗中选择另一份配置。

`LoadSelected()` 随后调用 `Load(SelectedPath())`。App 创建一次 `ServerConfiguration`，由它解析 `server.yaml` 及 include，校验后注册相同实例与持有的类型选项；运行期间不反复读文件或自动热重载。

控制台启动器的默认选择发生在启动器一侧：没有设置选择变量时，控制台可用仓库的 `ServerConfiguration.ConfigurationPath`。不能把这个默认值误认为服务器 `SelectedPath` 自己也会回退。

启动器把选中的源配置绝对路径传给子进程，不展开保存配置快照。补起新进程时仍读取源配置，因此修改源文件不会立即改变已运行进程，却可能影响后来补起的进程。

## 3. RunDirectory：本轮运行标记放在哪里

App 读取运行目录时：

```csharp
var runDirectory = Environment.GetEnvironmentVariable(EngineEnvironment.RunDirectory);
if (string.IsNullOrWhiteSpace(runDirectory))
{
    runDirectory = configuration.RunDirectory;
}
```

和 `Config` 不同，这个变量未指定或为空白时，会使用已加载配置中的 `RunDirectory`。之后 App 把路径规范化并要求目录已存在，缺少目录会抛异常。

该目录用于实例身份文件和停止标记，具体文件名见 [ServerRunFiles.cs](ServerRunFiles.cs.md)。它不是日志目录，也不是保存展开 YAML 的位置。

通过环境传递本轮目录，可以让不同启动批次使用不同运行标记，而不必修改源配置里的 `runDirectory`。传入路径和配置写回是两件事，当前加载器没有文件写回接口。

## 4. LogRoot：文件日志根目录

[GameLog.cs](../GameLog.cs.md) 在 `OpenServerCore` 中读取：

```csharp
var root = Environment.GetEnvironmentVariable(EngineEnvironment.LogRoot) ?? Path.Combine("logs", "server");
```

`??` 只在环境读取结果为 `null` 时回退，和前面 `IsNullOrWhiteSpace` 的条件不同。不能把三种变量的缺省规则概括成完全相同。

默认 `logs/server` 是相对当前工作目录的路径。随后日志再建立 `<实例名>Server` 子目录，例如 `Login1Server`，并用 PID 与进程启动时间组织文件前缀。

环境变量在打开文件时读取。文件打开之后，改环境变量不会自动让现有 logger 切换目录；`GameLog` 也拒绝再次 `OpenServer`。

## 5. 启动器怎样把值传给新进程

`ConsoleSession.StartInfo` 中有：

```csharp
info.Environment[EngineEnvironment.Config] = config;
info.Environment[EngineEnvironment.RunDirectory] = Path.Combine(sessions, name);
info.Environment[EngineEnvironment.LogRoot] = logRoot;
```

`info` 是 `ProcessStartInfo`。它的 `Environment` 是为将要启动的子进程准备的环境字典。方括号按键设置值，不是在修改 `EngineEnvironment` 的常量。

启动后，子进程通过 `GetEnvironmentVariable` 读取这些值。父子进程拥有独立内存，不能把它当作双方持有同一个 C# 字典；父进程后来改自己的变量，也不会自动改写子进程已经收到的环境。

控制台实现见 [ConsoleSession.cs](../../../../tools/server-console/ConsoleSession.cs.md)。

## 6. 状态、异常与范围

这个常量类没有可变状态和清理方法。读取、缺省、路径校验和目录创建的异常由各个使用方处理，不能把常量定义当成路径已经有效的保证。

这里三项都属于引擎启动和运行路径。数据库凭据环境变量由业务共享声明处理，不应把存储密码直接写进这些常量或路径配置。

[LogLevelChecks.cs](../../../tests/engine/LogLevelChecks.cs.md) 会临时设置 `LogRoot` 并在 `finally` 恢复旧值，用来隔离测试输出；测试修改当前进程的环境，也说明这类操作不宜与依赖相同环境的用例并发执行。
