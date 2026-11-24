# ServerLogLevel.cs 代码详解

源码：[code/server/engine/logging/ServerLogLevel.cs](../../../../../../code/server/engine/logging/ServerLogLevel.cs)

这份文件只有一个枚举，但它连接了三种表示：C# 中的日志级别、YAML 中的大写文本，以及 Serilog 库自己的级别。先把这三层分清，再读 [GameLog.cs](../GameLog.cs.md) 的过滤过程会容易得多。

## 1. enum 定义的是一组有名字的值

```csharp
namespace TGame;

public enum ServerLogLevel
{
    Debug,
    Info,
    Error
}
```

`namespace TGame;` 是文件作用域命名空间，本文件后面的类型都归入 `TGame`。命名空间与文件所在项目不是一回事；虽然名字是 `TGame`，本文件仍编译在 Engine 中，不依赖业务 Shared。

`public` 让其他程序集可以使用这个类型。`enum` 定义枚举；调用方写 `ServerLogLevel.Info`，编译器就知道这是一个日志级别，不是任意字符串。

未指定底层类型时，C# 枚举使用 `int`。未给成员显式编号时，从零递增，因此这里依次是 `Debug = 0`、`Info = 1`、`Error = 2`。这些数字是当前声明的结果，不应在调用方拿 `1` 代替 `ServerLogLevel.Info`。

下面是帮助理解的例子，不是本文件新增代码：

```csharp
ServerLogLevel level = ServerLogLevel.Info;
string name = level.ToString(); // "Info"
```

变量 `level` 保存的是枚举值，`ToString()` 才把它转换成成员名称。

## 2. 级别是事件的重要程度，也是过滤门槛

`GameLog.Debug(...)`、`Info(...)`、`Error(...)` 分别给单条事件选择级别。`GameLog.SetMinimumLevel(...)` 则设置“哪些级别可以输出”的门槛。这两个位置都用同一个枚举，但用途不同。

如果最低级别是 `Info`，`Debug` 被过滤，`Info` 与 `Error` 保留；门槛是 `Error` 时，只保留错误。默认调用顺序或事件名称不决定严重程度，例如叫 `ZkDiscover` 的事件既可以作为调试信息，也可以作为非法发现数据的错误日志。

| 最低级别 | 保留的级别 |
| --- | --- |
| `Debug` | `Debug`、`Info`、`Error` |
| `Info` | `Info`、`Error` |
| `Error` | `Error` |

这张表对应 [LogLevelChecks.cs](../../../tests/engine/LogLevelChecks.cs.md) 三轮样本的预期；具体过滤判断发生在 `GameLog.Write`，不是枚举声明自身自动执行的功能。

## 3. 为什么没有直接使用 Serilog 的枚举

`GameLog` 把这里的值显式映射到库类型：

```csharp
private static LogEventLevel ToSerilogLevel(ServerLogLevel level) => level switch
{
    ServerLogLevel.Debug => LogEventLevel.Debug,
    ServerLogLevel.Info => LogEventLevel.Information,
    ServerLogLevel.Error => LogEventLevel.Error,
    _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown server log level.")
};
```

方法返回 `LogEventLevel`。`level switch` 是 switch 表达式，匹配左侧模式后，使用右侧表达式作为结果。最后的 `_` 匹配其他情况。

`Info` 在 Serilog 中叫 `Information`，这已经说明两套枚举不能按名字或数字想当然地互换。项目仅暴露需要的三个级别，配置和 JSON 不必跟随第三方库的完整枚举变化。实际比较发生在映射后的 Serilog 级别之间。

## 4. 为什么枚举仍然需要校验

C# 可以写出如下显式转换；这只是语法示例：

```csharp
var unknown = (ServerLogLevel)999;
```

即使没有名为 `999` 的成员，变量仍能承载这个底层数字。因此“参数类型是枚举”不等于“参数值一定是已声明成员”。

`ServerConfiguration` 在加载后调用 `Enum.IsDefined(LoggingMinimumLevel)`，拒绝未定义值；`GameLog` 的映射方法也通过末尾的异常拒绝未知值。前者保护配置输入边界，后者保护日志服务入口。

配置绑定中的默认值位于 `ConfigurationDocument`：

```csharp
public ServerLogLevel LoggingMinimumLevel { get; init; } = ServerLogLevel.Info;
```

`{ get; init; }` 表示值可在初始化阶段设置，随后通常只读。右侧初始化器明确选择 `Info`，所以缺省配置为 INFO，不能从枚举首项是 `Debug` 推导配置缺省是 DEBUG。仓库测试配置显式写了 DEBUG，这又是另一回事。

YAML 加载、缺省和非法文本检查见 [ServerConfiguration.cs](../configuration/ServerConfiguration.cs.md) 与 [LogLevelChecks.cs](../../../tests/engine/LogLevelChecks.cs.md)。

## 5. 写进 JSON 时怎样变为大写

[LogEventJson.cs](LogEventJson.cs.md) 使用：

```csharp
level.ToString().ToUpperInvariant()
```

先把枚举变为成员名，再按不依赖当前文化环境的规则转大写，于是得到 `DEBUG`、`INFO`、`ERROR`。这一步只负责呈现，没有再次检查枚举是否合法；正常 `GameLog` 路径已经先执行映射校验。

这个文件本身没有对象状态、异步任务或可释放资源。它规定可使用的值，过滤、验证和输出仍由对应的使用方执行。
