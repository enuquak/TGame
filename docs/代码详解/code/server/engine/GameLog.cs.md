# GameLog.cs 代码详解

源码：[code/server/engine/GameLog.cs](../../../../../code/server/engine/GameLog.cs)

`GameLog` 是进程共享的日志服务。它把项目的三种级别转换成 Serilog 级别，把字段组成一行 JSON，再决定写标准输出、文件，还是额外写标准错误。理解本文件时要同时跟住日志对象的生命周期和一条日志的执行路径。

## 1. 先把 App 的调用顺序放在眼前

[Program.cs](../app/Program.cs.md) 在配置加载前创建：

```csharp
using var log = new GameLog();
```

`new` 创建一个日志对象，`var` 让编译器推断局部变量类型为 `GameLog`。`using var` 表示退出所在作用域时调用它的 `Dispose()`，不是声明后立刻释放。

随后 App 加载配置并设置级别，把同一个对象注册到根容器，初始化 ZooKeeper 并认领进程身份，然后才调用 `OpenServer(process)`。这个顺序有具体原因：配置加载或容器创建失败时，日志对象已经存在，可以把失败写到终端；文件目录又需要认领后的实例名，所以文件不能一开始就按猜测的编号打开。

正常的启动调用链可以读成：

```text
创建 GameLog，尚无文件 logger
  → 加载配置并设置最低级别
  → 把现有 GameLog 实例注册到根容器
  → ZooKeeper 认领，例如得到 Login1
  → OpenServer(Login1)，此后普通日志写文件
  → 初始化 ID、监听、连接依赖
  → 发布服务地址并记录 PROCESS_READY
```

容器中的服务通过构造函数接收同一个 `GameLog` 并调用实例方法。类里没有静态 `Instance` 或全局服务定位器。测试仍可以手动创建多个 `GameLog`，它们各有状态；IoC 单例是 App 的组装选择，不是类本身禁止创建第二个对象。

退出时 App 先取消并等待后台任务、停止监听，再异步释放容器，最后由外层 `using var log` 释放日志。这个外部创建的对象由 App 显式负责释放，服务使用者不能各自释放注入的日志。参见 [ServerServices.cs](../app/ServerServices.cs.md)。

## 2. 类声明和字段分别保存什么

```csharp
public sealed class GameLog : IDisposable
{
    private const long FileSizeLimit = 10 * 1024 * 1024;
    private bool disposed;
    private readonly object changes = new();
    private string? processName;
    private Logger? serverLog;
    private readonly LoggingLevelSwitch minimumLevel = new();
```

`public` 允许其他程序集使用，`sealed` 禁止继承。冒号后的 `IDisposable` 是接口，要求类提供同步清理入口 `Dispose()`。`GameLog` 本身不是静态类，因此下面的非静态字段属于每一个对象。

`FileSizeLimit` 是字节数常量，`10 * 1024 * 1024` 为 10 MiB。`long` 是 64 位整数，和 Serilog 接受的大小参数相配。

`disposed` 默认是 `false`，表示实例尚未进入释放流程。它记录生命周期历史；不能简单用 `serverLog == null` 替代，因为尚未打开文件的日志仍是有效对象。

`changes` 是专门用来加锁的对象。`readonly` 表示字段引用不能在普通方法中被替换，并不表示被引用对象的所有内容都不可变。`new()` 是目标类型推断写法，左侧已有 `object`，因此等价于 `new object()`。

`string? processName` 和 `Logger? serverLog` 中的 `?` 是可空引用类型标注。刚创建 `GameLog` 时两者为 `null`：前者尚无认领后的实例名，后者尚无文件 logger。这不是 `Nullable<int>` 那种可空值类型包装。

`minimumLevel` 是当前对象独有的 `LoggingLevelSwitch`。字段引用不变，但其 `MinimumLevel` 属性可以改变。文件 logger 与外层过滤都使用这一个开关，避免分别保存两份容易不同步的级别。

## 3. OpenServer 的公开入口保护生命周期变化

```csharp
public void OpenServer(ServerProcess server)
{
    lock (changes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        OpenServerCore(server);
    }
}
```

参数 `server` 是已认领的身份对象，包含类型和编号，例如 `Login1`。方法返回 `void`，同步完成或抛异常，没有返回供调用方等待的 `Task`。

`lock (changes)` 保证同一个日志对象上，使用这把锁的开文件、改级别和释放操作互斥。同一时间只有一个线程能执行这几个锁块中的代码。离开锁块时会释放锁，抛异常时也会释放。

`ObjectDisposedException.ThrowIf(disposed, this)` 的含义是：若已释放则抛 `ObjectDisposedException`，后面的开文件步骤不再执行。`this` 指当前 `GameLog` 对象，用于异常信息。

这把锁不覆盖所有写日志行为，`Write` 没有获取它。先记住这个边界，后面分析发布与清理时还会用到。

## 4. OpenServerCore 先拒绝重复打开

```csharp
if (serverLog is not null)
{
    throw new InvalidOperationException("The server log file is already open.");
}
```

`is not null` 是模式判断，表示引用已有对象。一个 `GameLog` 只能打开一个服务器文件 logger，不支持再次调用来切换身份或根目录。

为什么不直接释放旧 logger 再换新的？当前 App 的日志身份随一次进程认领确定，文件开关没有设计成运行期重配置入口。明确拒绝重复调用可以暴露错误的启动顺序。

正常调用都先经过持锁的公开入口，所以两个并发 `OpenServer` 不会同时越过这个检查。私有方法 `OpenServerCore` 不自行再加一次锁。

## 5. 用操作系统身份构造文件路径

```csharp
using var process = Process.GetCurrentProcess();
long startedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
var root = Environment.GetEnvironmentVariable(EngineEnvironment.LogRoot) ?? Path.Combine("logs", "server");
var directory = Directory.CreateDirectory(Path.Combine(root, server.Name + "Server"));
var path = Path.Combine(directory.FullName, $"{Environment.ProcessId}-{startedUtcTicks}-.log");
```

这里局部变量 `process` 是 `System.Diagnostics.Process` 包装对象，参数 `server` 则是项目的 `ServerProcess` 身份，不能混为一谈。前者给出操作系统启动时间，后者给出 `Login1` 这样的业务实例名。

`StartTime.ToUniversalTime().Ticks` 把进程启动时间转换为 UTC 再取刻度；每个刻度是 100 纳秒。不是调用当前时钟得到“日志打开时间”。PID 可能复用，加上启动时间可以把前后两次进程生命周期区分开。

`using var process` 在 `OpenServerCore` 退出时释放 `Process` 包装对象，即使后面失败也会尝试清理。这个释放不会结束当前服务器进程。

`GetEnvironmentVariable` 使用 [EngineEnvironment.cs](configuration/EngineEnvironment.cs.md) 中的日志根键。如果结果是 `null`，`??` 才使用默认 `logs/server`。这不是空白字符串校验；默认相对路径按当前工作目录解释。

`Path.Combine` 负责拼接路径分隔符。`Directory.CreateDirectory` 创建目录或取得已经存在的目录，并返回 `DirectoryInfo`；`directory.FullName` 是其完整路径。

对于 `Login1`，目录名是 `Login1Server`。下面仅是形状示意，不是本轮生成的文件：

```text
<LogRoot>/Login1Server/4020-639000000000000000-.log
```

`$"..."` 是插值字符串，将 PID 和启动刻度放入路径。末尾的 `-.log` 给 Serilog 的滚动命名留出位置，最终文件名还会带按小时滚动的日期文字以及必要的大小分片后缀。

目录创建、读取进程信息或构建 logger 过程中，传播出来的异常会结束开文件操作；本方法没有吞掉这些异常的 `catch`。

## 6. Serilog 的配置链逐步做了什么

```csharp
var logger = new LoggerConfiguration().MinimumLevel.ControlledBy(minimumLevel)
    .WriteTo.File(path, outputTemplate: "{Message:lj}{NewLine}",
    rollingInterval: RollingInterval.Hour, rollOnFileSizeLimit: true, fileSizeLimitBytes: FileSizeLimit,
    retainedFileCountLimit: null).CreateLogger();
```

这是一条配置构建链。`new LoggerConfiguration()` 先得到配置对象；`MinimumLevel.ControlledBy(minimumLevel)` 把最低级别交给前面的可变开关；`WriteTo.File(...)` 添加文件输出；`CreateLogger()` 最后按这些设置创建可用 logger。

这里不是“每调用一个链式方法都写一行日志”。这些调用是在设置输出方式，真正写记录发生在 `logger.Write`。

命名参数的含义分别是：

- `outputTemplate` 仅输出消息和换行，避免再包一层默认时间、级别前缀，因为 JSON 已自带这些字段。
- `rollingInterval: RollingInterval.Hour` 按小时滚动文件。
- `rollOnFileSizeLimit: true` 允许达到大小门槛后继续滚动到新分片。
- `fileSizeLimitBytes: FileSizeLimit` 把门槛设为 10 MiB；不应把它解释成每个文件绝不会因单条记录越过门槛的逐字节截断器。
- `retainedFileCountLimit: null` 取消按保留文件份数自动删旧文件的限制，不是“系统自动安排了无限磁盘空间”。

这些是当前写入配置；[LogLevelChecks.cs](../../tests/engine/LogLevelChecks.cs.md) 验证小样本写入，不会为了检查这些参数而专门制造小时边界或 10 MiB 文件。

## 7. 先写实例名，再发布文件 logger

```csharp
processName = server.Name;
Volatile.Write(ref serverLog, logger);
```

只有 logger 已创建成功，才把实例名保存到 `processName`。第二行使用 `Volatile.Write` 把对象引用写入字段。

`ref serverLog` 传递字段的存储位置，而不是普通地复制一次引用值。这样方法能对这个字段执行带内存可见性语义的写入。

写日志时用配对的 `Volatile.Read(ref serverLog)`。线程如果读到已发布的 logger，也应能看到发布前完成的初始化，包括实例名赋值。这里“发布”指跨线程可见性，不是向 ZooKeeper 发布服务地址。

`Volatile` 不把整条写日志流程变成一个锁定事务，也不替代生命周期管理。特别是在开文件与写入故意并发的场景，不能只凭这两行推导所有日志都有同一精确切换点；App 的启动流程本身按顺序调用。

## 8. SetMinimumLevel 修改同一个开关

```csharp
public void SetMinimumLevel(ServerLogLevel level)
{
    lock (changes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        minimumLevel.MinimumLevel = ToSerilogLevel(level);
    }
}
```

开文件前后都可以设置级别。`ToSerilogLevel(level)` 先执行，成功后才给属性赋值；若传入未知枚举值，映射抛异常，赋值不会完成。

外层过滤读取这个开关，文件 logger 也在构建时受它控制。因此不需要重建文件对象来应用一次级别调整。

它与 `OpenServer`、`Dispose` 使用同一把锁，所以不能在释放操作的锁块中途再改级别。类没有从 YAML 自行加载级别；App 从唯一的 `ServerConfiguration` 读取配置后主动调用这个方法。

## 9. 三个公开写入入口只是选择级别

```csharp
public void Info(string ev, string role, string? peer = null, string? id = null,
    string? entity = null, string? link = null, string? detail = null) =>
    Write(ServerLogLevel.Info, ev, role, peer, id, entity, link, detail);
```

方法参数后的 `=>` 表示表达式体方法，这里执行一次 `Write(...)`。它不是 lambda 参数，也不代表异步执行。

`ev` 和 `role` 必传，其余参数可省略并默认为 `null`。调用方可以写 `detail: "开始监听"`，用命名参数跳过中间可选项。

`Debug` 的结构相同，只传 `ServerLogLevel.Debug`。`Error` 多一个开关：

```csharp
public void Error(string ev, string role, string? peer = null, string? id = null,
    string? entity = null, string? link = null, string? detail = null, bool bothPrintConsole = false) =>
    Write(ServerLogLevel.Error, ev, role, peer, id, entity, link, detail, bothPrintConsole);
```

`bool` 是布尔类型。默认 `false` 表示使用普通去向；显式传 `true` 请求把这条错误写到标准错误。当前公开接口只有 Error 暴露这个选项。

这些入口都没有 `Task` 或后台队列。调用会同步进入过滤、序列化和输出调用；不能把 logger 的存在理解成“自动建立了独立日志线程”。

## 10. Write 首先检查生命周期和门槛

```csharp
ObjectDisposedException.ThrowIf(disposed, this);
var severity = ToSerilogLevel(level);
if (severity < minimumLevel.MinimumLevel)
{
    return;
}
```

先检查已释放，所以释放后即使准备写一条本应被过滤的 Debug，也不允许继续使用对象。生命周期错误不会被过滤逻辑隐藏。

`severity` 是映射后的 Serilog 级别。比较小于当前最低级别时提前返回，后续 JSON 构造和输出都不执行。例如门槛 Info 下写 Debug，不仅没有文件行，也不会先构造时间和字典再丢弃。

门槛比较发生在镜像分支之前，所以 `bothPrintConsole` 没有绕过过滤的特权。

## 11. 映射方法为什么要列出三个分支

```csharp
private static LogEventLevel ToSerilogLevel(ServerLogLevel level) => level switch
{
    ServerLogLevel.Debug => LogEventLevel.Debug,
    ServerLogLevel.Info => LogEventLevel.Information,
    ServerLogLevel.Error => LogEventLevel.Error,
    _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown server log level.")
};
```

`switch` 表达式按匹配结果返回右侧值。项目的 `Info` 对应库的 `Information`，所以不靠底层枚举整数强制转换。

`_` 匹配其余情况，`nameof(level)` 得到参数名字符串。C# 可以通过强制转换制造 `(ServerLogLevel)999`，枚举参数并不自动保证值合法，兜底异常保护这个边界。

`static` 表示方法不依赖某个日志对象的字段，适合这种纯映射；不能据此说整个日志服务是静态服务。

## 12. 通过过滤后只生成一次 JSON

```csharp
var logger = Volatile.Read(ref serverLog);
var json = LogEventJson.Serialize(level, ev, role, peer, id, entity, link, detail, processName);
```

先把文件 logger 引用读到局部变量，再调用序列化工具。局部引用用于本次路由判断，后面不会重复读取字段来换目标。

[LogEventJson.cs](logging/LogEventJson.cs.md) 生成时间、级别、事件、角色、实例、PID 及关联字段。文件打开前 `processName` 为 `null`，正常顺序下 `instance` 回退为类型名；打开后使用认领后的名字。

序列化结果只保存一份。文件和终端镜像使用同一个 `json`，所以时间、字段与转义内容完全相同；没有分别调用两次序列化造成时间戳差异。

例如 `Login1` 的普通事件仍可以有 `role = Login`。进程类型、具体实例和操作系统 PID 在同一记录里各有字段，不能互相代替。

## 13. 路由分支逐步执行，而不是同时打印所有目标

```csharp
if (logger is not null)
{
    logger.Write(severity, "{Json:l}", json);
}
else if (!bothPrintConsole)
{
    Console.WriteLine(json);
}

if (bothPrintConsole)
{
    Console.Error.WriteLine(json);
}
```

第一组 `if/else if` 先决定文件或启动期标准输出：有 logger 就写文件；没有 logger 且没有请求镜像，才写 stdout。第二个独立 `if` 再处理标准错误。

因此一条通过过滤的记录只有以下去向：

| 文件是否打开 | 镜像开关 | 实际调用 |
| --- | --- | --- |
| 否 | `false` | `Console.WriteLine`，一条 stdout |
| 否 | `true` | `Console.Error.WriteLine`，一条 stderr |
| 是 | `false` | 文件 |
| 是 | `true` | 文件加一条 stderr |

开文件前开关为真时，`else if (!bothPrintConsole)` 不成立，所以不会先写 stdout 再写 stderr。开关名字含“同时”，但在没有文件目标时，它的效果是只向 stderr 写一次。

`Console.Out` 和 `Console.Error` 是两条不同的标准流，测试可分别重定向；终端可能把两条流显示在同一窗口，并不意味着程序重复写了两次同一流。

## 14. JSON 为什么不会被包成带引号的普通消息

```csharp
logger.Write(severity, "{Json:l}", json);
```

第二个参数是 Serilog 消息模板，`Json` 是模板属性名，`:l` 要求字符串按字面形式呈现。这里要的是一行完整 JSON，而不是把整个 JSON 再当成普通字符串加引号转义。

前面文件输出模板是 `{Message:lj}{NewLine}`，将呈现后的消息写出并补换行。时间和级别已经在 JSON 内，不再由文件模板另加一组前缀。

如果详情本身包含引号、中文或换行，`LogEventJson` 会负责合法 JSON 转义。详情里的换行不会变成额外的物理记录行，文件读取方才能逐行 `JsonDocument.Parse`。

## 15. Dispose 怎样结束对象生命周期

```csharp
public void Dispose()
{
    lock (changes)
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        serverLog?.Dispose();
    }
}
```

第一次调用先将 `disposed` 设为真，再释放已有文件 logger。`?.` 是空条件调用：若尚未打开文件，`serverLog` 为 `null`，便不调用其方法，但外层对象仍进入已释放状态。

第二次释放发现标记已真，直接返回。这是重复释放安全，也称幂等释放。字段 `serverLog` 没有被清空不代表可继续使用；判断生命周期的权威来源是 `disposed`。

标记在底层释放前设置，意味着即使底层释放异常，外层也不重新变回可用状态。该方法没有清理异常捕获；异常能否传播还取决于底层 logger 的行为。

释放 logger 使其结束所持有的文件输出资源，因此测试先离开 `using` 块，再读取落盘文件。释放不是删除日志文件，已有日志仍保留供诊断。

## 16. 并发保障与没有承诺的部分

开文件、设置级别、释放三类变化通过 `changes` 互斥；文件 logger 引用用 `Volatile` 发布和读取。但 `Write` 没有拿锁，`disposed` 检查、过滤、序列化和输出不是不可分割的操作。

因此不能声称“一个线程在写日志时，另一个线程可以任意释放并且每条都保证落盘”。App 先结束后台工作再释放日志，正是调用方应守住的生命周期边界。

Serilog 的某些内部输出失败可能走其 SelfLog 诊断通道；App 配置了 `Serilog.Debugging.SelfLog` 输出到标准错误。不能仅凭本类没有 `catch` 就说任何文件写入问题一定向调用方抛出异常。

[LogLevelChecks.cs](../../tests/engine/LogLevelChecks.cs.md) 验证过滤、启动期去向、文件 JSON、镜像一致、重复打开、释放后拒绝使用以及实例隔离。它没有并发写入与释放竞争、磁盘耗尽或小时滚动的专门场景。本文解释的是源码行为和测试意图，不代表本轮实际运行过检查。
