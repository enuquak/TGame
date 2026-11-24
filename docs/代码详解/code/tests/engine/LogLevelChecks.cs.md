# LogLevelChecks.cs 代码详解

源码：[code/tests/engine/LogLevelChecks.cs](../../../../../code/tests/engine/LogLevelChecks.cs)

这份检查程序同时验证日志配置、级别过滤、JSON 内容、输出去向和对象生命周期。它不是对每个方法分别做一句“应该成功”的检查，而是先搭好可观察的环境，再用内存里的终端输出或临时目录里的文件判断实际行为。

阅读顺序可以是：理解准备与辅助方法，跟随没有文件的场景，再看文件与镜像，最后看实例释放和清理。被测实现是 [GameLog.cs](../../server/engine/GameLog.cs.md)。

## 1. 入口、类型与测试依赖

```csharp
internal static class LogLevelChecks
{
    public static void Run()
```

`internal` 表示类供当前程序集内部使用。`static` 表示调用方直接写 `LogLevelChecks.Run()`，无需创建测试类对象。`void` 表示方法正常结束时不返回结果；检查失败时通过异常传回调用方。

这里没有 `async`、`Task` 或 `await`。日志调用、临时文件读写和断言都按同步控制流执行。测试入口 [Program.cs](Program.cs.md) 在 `--logging` 分支直接调用它，然后返回退出码；这个分支位于构造其他测试服务之前，不需要启动 ZooKeeper 或数据库。

文件顶部引用的 `System.Text.Json` 用于解析每行 JSON，`YamlDotNet` 用于构造配置样本和识别 YAML 异常。引入引擎命名空间不代表检查会启动真实服务进程。

## 2. 准备临时目录与身份样本

```csharp
var root = Path.Combine(Path.GetTempPath(), $"tgame-log-levels-{Guid.NewGuid():N}");
var process = new ServerProcess(ProcessType.Login, 1);
var originalRoot = Environment.GetEnvironmentVariable(EngineEnvironment.LogRoot);
var originalOutput = Console.Out;
var originalError = Console.Error;
Directory.CreateDirectory(root);
```

`Path.GetTempPath()` 取得系统临时目录；`Guid.NewGuid()` 创建随机标识，插值字符串里的 `:N` 选择不含连字符的 GUID 格式。每次检查使用不同子目录，减少与其他运行产物重名的机会。

`process` 是身份数据，不是操作系统进程。`ProcessType.Login` 是类型，编号为 1，因此 `process.Role` 是 `Login`，`process.Name` 是 `Login1`；不会因 `new ServerProcess(...)` 启动一个 Login 服务。

接下来的三项保存全局环境原值，因为测试会临时替换它们：日志根目录环境变量、标准输出 writer、标准错误 writer。保存 `Console.Out` 是保存原 writer 的引用，没有复制终端内容。

`Directory.CreateDirectory(root)` 在外层 `try` 之前执行。后面的 `finally` 保护的是成功进入 `try` 后的测试与清理，不应笼统说连这一步尚未完成时也一定会执行该 `finally`。

## 3. StringWriter 怎样让“输出”变成可断言的数据

后面反复使用这种模式：

```csharp
using var output = new StringWriter();
Console.SetOut(output);
using var log = new GameLog();
```

`StringWriter` 把文字写到内存，不写真实终端。`Console.SetOut(output)` 替换当前进程的标准输出目标，因此 `GameLog` 调用 `Console.WriteLine` 时，文字进入这个 writer。

`output.ToString()` 取出迄今收到的全部文本。再按换行切分，就能判断有几条日志、顺序是什么、每条是否为合法 JSON。

`using var` 在声明所在作用域结束时释放对象。循环体中声明的对象每轮释放，单独花括号块里的对象在出块时释放；不是所有对象都一直活到 `Run` 返回。

同一作用域内按声明逆序释放，例如先创建 writer、再创建 logger，退出时先释放 logger 再释放 writer，避免日志对象正常清理时依赖已提前释放的输出对象。

`Console.SetOut` 改的是整个进程共享的标准流，不是只影响某个 `GameLog`。检查按顺序执行，不能与也在使用这些标准流或日志根变量的其他检查随意并行。

## 4. WriteSamples 固定了样本顺序

```csharp
private static void WriteSamples(GameLog log, ServerProcess process)
{
    log.Debug(ServerLogEvents.ZkDiscover, process.Role, detail: "发现细节");
    log.Info(ServerLogEvents.Listen, process.Role, detail: "开始监听");
    log.Error(ServerLogEvents.ProcessFailed, process.Role, detail: "进程失败");
}
```

方法接收已准备好的 logger 和身份，不自行创建或释放它们。每次严格按 Debug、Info、Error 的顺序写三条，详情都非空。

`detail:` 是命名参数，可以跳过 `peer`、`id` 等可选参数。事件名称与日志级别是两个维度：`ZkDiscover` 表示发生了什么，`Debug` 表示重要程度。

固定顺序使后面不仅能验证条数，还能逐位置验证级别。最低 Info 时，第一条被过滤，剩下的自然顺序就是 INFO、ERROR。

## 5. CheckRows 先建立正确答案，再比较实际条数

```csharp
private static void CheckRows(string[] lines, ServerLogLevel minimum, string instance)
{
    string[] expected = minimum switch
    {
        ServerLogLevel.Debug => ["DEBUG", "INFO", "ERROR"],
        ServerLogLevel.Info => ["INFO", "ERROR"],
        ServerLogLevel.Error => ["ERROR"],
        _ => throw new ArgumentOutOfRangeException(nameof(minimum))
    };
    if (lines.Length != expected.Length)
    {
        throw new Exception($"Minimum level {minimum} wrote {lines.Length} rows, expected {expected.Length}.");
    }
```

`string[]` 是字符串数组。`minimum switch` 是 switch 表达式，按门槛选出预期数组。`["INFO", "ERROR"]` 是集合表达式，目标类型已知为 `string[]`，因此构造相应数组。

末尾 `_` 捕获未知门槛并抛异常，避免辅助方法自己默默接受非法测试参数。

先比较行数很重要：只逐行查看现有行，可能漏掉“少写一条”，甚至零行时循环根本不执行。行数断言要求精确等于预期，多写和少写都会失败。

例如最低 Info 时收到三行，表示过滤没有挡住 Debug，或出现了额外记录；只收到一行，则可能漏写了 INFO 或 ERROR。具体是哪一种问题，还要结合失败内容定位。

## 6. CheckRows 如何逐行解释 JSON 与每个断言

```csharp
for (int index = 0; index < lines.Length; index++)
{
    using var row = JsonDocument.Parse(lines[index]);
    var value = row.RootElement;
    if (value.GetProperty(ServerLogFields.Level).GetString() != expected[index] ||
        value.GetProperty(ServerLogFields.Instance).GetString() != instance ||
        value.GetProperty(ServerLogFields.Role).GetString() != nameof(ProcessType.Login) ||
        value.GetProperty(ServerLogFields.Pid).GetInt32() != Environment.ProcessId ||
        string.IsNullOrEmpty(value.GetProperty(ServerLogFields.Event).GetString()) ||
        string.IsNullOrEmpty(value.GetProperty(ServerLogFields.Detail).GetString()))
    {
        throw new Exception($"Unexpected log fields at minimum level {minimum}: {lines[index]}");
    }
}
```

`for` 从索引 0 开始，读到 `Length - 1` 为止。`JsonDocument.Parse` 将一条物理日志行解析为 JSON 文档；非法 JSON 会在解析时失败，无需等到后面的字段检查。

`using var row` 让每轮结束时释放文档。`RootElement` 是指向该文档根值的视图，字段读取都在文档仍有效时进行，没有把它保存到释放之后再用。

`GetProperty` 要求键存在；`GetString` 和 `GetInt32` 要求值类型合适。缺键或类型错误同样会令检查失败，不一定走到自定义的 `Unexpected log fields` 异常。

这六项依次验证：

1. `level` 等于当前位置的预期级别，既检查保留内容，也检查顺序。
2. `instance` 等于调用方给的预期值，区分文件打开前的 `Login` 与打开后的 `Login1`。
3. `role` 始终为 `nameof(ProcessType.Login)`，即 `Login`，不能把实例名误写成类型名。
4. `pid` 等于执行测试的当前操作系统 PID，不能丢字段、写成字符串或使用实例编号 1。
5. `ev` 非空，确认事件标识没有丢失。
6. `detail` 非空，确认详情没有丢失。

`||` 是短路或：任一项不符就失败，后续条件不必继续求值。`nameof` 得到 C# 成员名，不需要创建对象，也不会得到底层枚举数字。

这里没有检查每个事件必须正好等于样本中的 ZK_DISCOVER/LISTEN/PROCESS_FAILED，也没有核对详情原文、时间字段或全部可选字段。它是一组针对当前目标的断言，不是完整日志协议校验器。

## 7. CheckSettings 先确认仓库样本显式为 DEBUG

`Run` 首先调用 `CheckSettings(root)`，这时尚未替换控制台流。辅助方法开头是：

```csharp
var source = Path.Combine(AppContext.BaseDirectory, "config", ServerConfiguration.FileName);
if (ServerConfiguration.Load(source).LoggingMinimumLevel != ServerLogLevel.Debug)
{
    throw new Exception("The test environment must default to DEBUG.");
}
```

`AppContext.BaseDirectory` 是程序运行基目录，这里定位测试输出下复制的 `config/server.yaml`，并非随意使用终端当前工作目录。

`ServerConfiguration.FileName` 复用正式配置文件名常量。`Load` 走正式解析与验证流程，而不是测试自己用字符串搜索猜级别。

这里的 DEBUG 指测试环境样本显式配置，不是“缺少字段时的默认值”。若仓库样本改成其他值，这项断言会失败，提示测试前提变了。

## 8. 缺少字段时为何应回退为 INFO

```csharp
var path = Path.Combine(root, "server.yaml");
var values = new ServerConfiguration(source).Read<Dictionary<string, object>>();
var yaml = new SerializerBuilder().Build().Serialize(values).Replace("loggingMinimumLevel: DEBUG", "");
File.WriteAllText(path, yaml);
if (ServerConfiguration.Load(path).LoggingMinimumLevel != ServerLogLevel.Info)
{
    throw new Exception("An unspecified minimum log level must default to INFO.");
}
```

`Read<Dictionary<string, object>>()` 是泛型调用，要求把已加载合并后的配置绑定为字典。测试借助这份完整有效的数据建立样本，避免为了测试一个日志字段，手写并遗漏其他必填配置。

`new SerializerBuilder().Build()` 创建 YamlDotNet 序列化器；`Serialize(values)` 生成 YAML 文本；`Replace` 删除固定样例中的级别行。此处替换依赖本测试已知的样本文本形状，不是生产配置编辑器。

`File.WriteAllText` 把样本文本写进临时目录，然后正式加载器再次读取。缺失字段时，内部绑定类型的属性初始化值是：

```csharp
public ServerLogLevel LoggingMinimumLevel { get; init; } = ServerLogLevel.Info;
```

因此预期为 Info。`init` 允许构造/初始化阶段赋值，右边初始化器提供字段缺省。枚举第一项是 Debug，不代表配置也必须默认 Debug。

这段测试自己序列化字典并写临时文件，不是调用 `ServerConfiguration` 的写回接口；当前配置类没有写回接口，生产运行也不保存配置快照。见 [ServerConfiguration.cs](../../server/engine/configuration/ServerConfiguration.cs.md)。

## 9. 每个合法级别都通过文本绑定回来

```csharp
foreach (var minimum in Enum.GetValues<ServerLogLevel>())
{
    File.WriteAllText(path, yaml.Replace("...", "") + $"\nloggingMinimumLevel: {minimum.ToString().ToUpperInvariant()}\n");
    if (ServerConfiguration.Load(path).LoggingMinimumLevel != minimum)
    {
        throw new Exception($"Minimum log level {minimum} was not loaded.");
    }
}
```

`Enum.GetValues<ServerLogLevel>()` 取得这个枚举的所有已声明值；`foreach` 依次处理 Debug、Info、Error。每轮覆盖同一个临时文件，不会在旧文件末尾不断累积多个同名字段。

`yaml` 是前面已经去掉级别行的基础文本。`Replace("...", "")` 去掉可能的 YAML 文档结束标记，使新字段能放在有效文档中；这也是针对受控测试文本的做法。

插值前 `ToString().ToUpperInvariant()` 将成员变成 DEBUG、INFO、ERROR。加载后必须等于本轮原枚举值，因而不仅验证“文件能读”，还验证文字确实绑定到正确级别。

## 10. 非法级别必须失败，测试不能把自己的失败吞掉

```csharp
foreach (var invalid in new[] { "TRACE", "WARNING", "999" })
{
    File.WriteAllText(path, yaml.Replace("...", "") + $"\nloggingMinimumLevel: {invalid}\n");
    try
    {
        ServerConfiguration.Load(path);
        throw new Exception($"Invalid log level {invalid} was accepted.");
    }
    catch (InvalidDataException)
    {
    }
    catch (YamlException)
    {
    }
}
```

`new[]` 根据元素推断为字符串数组。TRACE、WARNING 检查未提供的级别名称，999 检查枚举数字输入越过已定义范围的情况。

如果加载在数据校验阶段抛 `InvalidDataException`，或 YAML 绑定阶段抛 `YamlException`，空 `catch` 表示这正是预期失败，本轮继续。

如果加载没有抛异常，紧接着测试自己抛普通 `Exception`。后面两个 `catch` 都不捕获这个普通异常，所以检查确实失败，不会误把“非法值被接受”算作通过。

若抛的是另外一种未列出的异常，也会传播，表示这不是本检查接受的正常拒绝路径。这组用例接受两类异常，不固定每个输入必须产生其中哪一种，也没有检查异常消息内容。

## 11. 未打开文件：三轮门槛分别输出多少行

回到 `Run`：

```csharp
foreach (var minimum in Enum.GetValues<ServerLogLevel>())
{
    using var output = new StringWriter();
    Console.SetOut(output);
    using var log = new GameLog();
    log.SetMinimumLevel(minimum);
    WriteSamples(log, process);
    CheckRows(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), minimum, process.Role);
}
```

每轮都创建新 `GameLog`，只设置级别，不调用 `OpenServer`，所以普通日志走标准输出。样本有三条，过滤后的行数依次应为 3、2、1。

`Environment.NewLine` 是当前系统换行序列。`Split(..., RemoveEmptyEntries)` 拆分记录，并去掉末尾换行留下的空项，避免把最后一个空字符串算作日志。

传给 `CheckRows` 的第三个参数是 `process.Role`，即 `Login`。因为还没有打开实例文件，`processName` 仍为空，JSON 的 `instance` 会回退为角色名；这里明确验证这种启动期行为。

只检查字段不够：如果普通日志误写到了文件，内存 writer 的行数就不符；如果不做门槛过滤，不同轮的数量或级别顺序会失败。

## 12. 未打开文件的错误镜像只写 stderr 一次

```csharp
using var output = new StringWriter();
using var error = new StringWriter();
Console.SetOut(output);
Console.SetError(error);
using var log = new GameLog();
log.Error(ServerLogEvents.ProcessFailed, process.Role, detail: "进程失败", bothPrintConsole: true);
CheckRows(error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), ServerLogLevel.Error, process.Role);
if (output.ToString().Length != 0)
{
    throw new Exception("Console mirroring duplicated an error on standard output before opening a log file.");
}
```

这段位于一个独立花括号块内。准备两份 writer，分别捕获 stdout 和 stderr，再写一条要求终端镜像的错误。

`CheckRows` 以 Error 门槛构造预期，实际上要求 stderr 恰好一条 ERROR，且实例为启动期的 Login。它不是在这里给 logger 设置门槛；第二个参数只是告诉断言“应看到什么”。

另一个断言要求 stdout 完全为空。这对应 `GameLog.Write` 的 `else if (!bothPrintConsole)`：没有文件且镜像为真时，跳过 stdout，最后只执行 stderr 输出。

若实现把错误同时写到 stdout 和 stderr，stderr 断言仍可能通过，但 stdout 为空的检查会失败。因此两项一起验证“错误能看到”和“没有重复到另一条标准流”。

## 13. 打开文件前，为每个门槛准备独立目录

```csharp
foreach (var minimum in Enum.GetValues<ServerLogLevel>())
{
    var logRoot = Path.Combine(root, minimum.ToString());
    using var output = new StringWriter();
    using var error = new StringWriter();
    Console.SetOut(output);
    Console.SetError(error);
    Environment.SetEnvironmentVariable(EngineEnvironment.LogRoot, logRoot);
    using (var log = new GameLog())
    {
        log.SetMinimumLevel(minimum);
        log.OpenServer(process);
```

每轮目录分别是临时根下的 Debug、Info、Error，避免将上一轮记录混入下一轮。`SetEnvironmentVariable` 改当前进程环境，`OpenServer` 随后读取该变量并在其下建立 `Login1Server`。

这里使用 `using (...) { ... }` 块形式，特意让 logger 的释放早于后面的文件读取。writer 的 `using var` 位于外层循环体，所以读文件和比较终端内容时，它们仍然存在。

`OpenServer` 是同步调用；它取得测试进程的 PID 与启动时间作为文件前缀，不会创建一个真正的 Login1 操作系统进程。

## 14. 重复打开必须报错，普通日志不能悄悄复制到终端

仍在 logger 的 `using` 块中：

```csharp
ExpectInvalidOperation(() => log.OpenServer(process), "Opening a second log file must fail.");
WriteSamples(log, process);
if (output.ToString().Length != 0 || error.ToString().Length != 0)
{
    throw new Exception("File logging printed to the console without an explicit request.");
}
```

第二次打开应抛 `InvalidOperationException`，因为 `serverLog` 已存在。检查之后继续用同一个 logger 写样本，也在观察被拒绝的重复操作没有破坏原对象的正常写入。

`WriteSamples` 没有请求镜像，所以输出必须全部进入文件。stdout 和 stderr 任何一个非空都会失败。

这个断言在随后显式镜像之前执行，因而能够把普通日志的意外终端输出与允许的镜像输出区分开；如果只在最后检查“stderr 有一条”，就难以定位多余输出来自哪一步。

## 15. 再写一条包含换行的镜像错误，然后释放文件 logger

```csharp
log.Error(ServerLogEvents.ProcessFailed, process.Role,
    detail: "同时写入日志与终端：'错误'\n详细信息", bothPrintConsole: true);
```

字符串中的 `\n` 在运行时详情里成为真实换行，同时包含中文和引号。序列化器必须把它编码成合法 JSON 内容，使整条记录仍只占一条物理日志行。

这条错误级别在 Debug、Info、Error 三种最低级别下都应保留。它是本轮最后写入的一条，用来单独比较文件与 stderr。

到 `using (var log ...)` 块结束时调用 `Dispose()`，先结束文件输出资源，再执行读取。这个释放顺序避免测试在正常写入仍未收尾时就下结论。

## 16. 从文件列表得到所有记录，范围语法怎样读

```csharp
var files = Directory.GetFiles(logRoot, "*.log", SearchOption.AllDirectories);
var lines = files.Order(StringComparer.Ordinal).SelectMany(File.ReadLines).ToArray();
CheckRows(lines[..^1], minimum, process.Name);
```

`GetFiles` 递归查找 `.log` 文件，因为真正的文件位于 `Login1Server` 子目录。`Order(StringComparer.Ordinal)` 按字符序号排序路径；它不是按文件创建时间排序。

`SelectMany(File.ReadLines)` 对每个文件调用 `ReadLines`，再把各个行序列展开为一个总序列。这里 `File.ReadLines` 是方法组，传给 LINQ 当作函数使用，相当于“给我文件路径，我返回这个文件的各行”。

`ToArray()` 真正枚举这些序列并把全部行放入数组；之前的 LINQ 步骤主要描述如何枚举。文件读取中的异常会在读取/枚举时传出，测试没有忽略它们。

`lines[..^1]` 是范围切片：从开头取到倒数第一个位置之前，排除最后一条镜像错误。`^1` 表示从末尾计算的最后一个位置；对数组切片会得到新的数组。

传入 `process.Name`，所以普通文件记录的 `instance` 必须为 `Login1`。这与前面的控制台阶段预期 Login 形成对照，验证打开文件时身份切换生效。

该检查只写少量记录，没有刻意制造滚动或跨小时；路径排序用于这里的样本读取，不应把它当作对任意滚动边界全局顺序的证明。

## 17. 文件与 stderr 必须是一模一样的一条 JSON

```csharp
var consoleLines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
CheckRows(consoleLines, ServerLogLevel.Error, process.Name);
if (output.ToString().Length != 0 || consoleLines[0] != lines[^1])
{
    throw new Exception("Console mirroring must write the same error record exactly once to standard error.");
}
```

先验证 `consoleLines` 恰好包含一条合法 ERROR，且实例为 Login1。这个调用已经检查数量，因此后面访问 `[0]` 具有测试前提；如果没有输出，会先由 `CheckRows` 报错。

`lines[^1]` 取得文件里的最后一行。与 stderr 第零行比较整段文字，不只比较 `ev` 或 `detail`，因此时间、PID、所有字段和转义形式都必须一样。

实现里只序列化一次、两处复用 `json`，正是这项断言的依据。如果分别序列化导致时间不同，即使语义都像“同一次错误”，全文比较仍会失败。

stdout 再次必须为空，保证显式镜像目标是 stderr。结合行数检查，这才得到“文件一条，stderr 一条，stdout 零条”的预期。

这里没有单独把详情解析回原始多行字符串并逐字比较；它直接验证逐行 JSON 可解析、详情非空和两个输出完全相同，不应扩大为全部字符编码用例都已覆盖。

## 18. Expect 辅助方法怎样把操作延后执行

重复打开使用的辅助方法是：

```csharp
private static void ExpectInvalidOperation(Action action, string message)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new Exception(message);
}
```

`Action` 是不接收参数、不返回结果的委托类型。调用处 `() => log.OpenServer(process)` 创建一个函数，交给辅助方法；传入时并未执行 `OpenServer`，真正调用在 `action()` 这一行。

`()` 是无参数 lambda，`=>` 右边是要执行的动作。lambda 捕获了外部的 `log` 和 `process`，所以辅助方法不用认识这两个具体对象也能执行操作。

抛出预期类型或其派生类型时，`catch` 返回，检查通过；抛其他异常则直接传播；根本不抛异常则执行最后一行，让测试失败。

因此这里检查的是异常类型范围，不是精确运行时类型、异常消息或同一个异常对象。尤其 `ObjectDisposedException` 继承 `InvalidOperationException`，会被这种 catch 接受；本重复打开场景在有效对象上执行，前置条件同样重要。

另一个辅助方法 `ExpectDisposed(Action action)` 结构相同，只捕获更具体的 `ObjectDisposedException`，没有异常时抛 `Using a disposed logger must fail.`。它用于下面三个生命周期入口。

## 19. CheckLifecycle 先制造两个独立实例

```csharp
using var output = new StringWriter();
var originalOutput = Console.Out;
Console.SetOut(output);
try
{
    var previous = new GameLog();
    using var current = new GameLog();
    previous.SetMinimumLevel(ServerLogLevel.Debug);
    current.SetMinimumLevel(ServerLogLevel.Info);
    previous.Dispose();
```

辅助方法自己捕获 stdout，并保存进入时的 writer，以便结束后归还。外层 `Run` 还负责最终恢复真正最初的标准流，两层清理各自恢复自己替换的对象。

`previous` 和 `current` 是两个不同的 `GameLog`。分别设为 Debug 和 Info，再先释放 previous，模拟一个旧服务已经结束而另一个仍在使用的情况。

每个实例应有自己的释放标记和级别开关。IoC 生产环境共享一个实例，不代表日志类可以用静态全局字段保存这些状态；测试显式创建多份对象正是检查这个边界。

## 20. 旧实例三个入口都必须拒绝使用

```csharp
ExpectDisposed(() => previous.Info(ServerLogEvents.Listen, process.Role));
ExpectDisposed(() => previous.SetMinimumLevel(ServerLogLevel.Info));
ExpectDisposed(() => previous.OpenServer(process));
previous.Dispose();
```

第一条验证已释放后不能写日志。`Write` 开头检查 `disposed`，应立即抛异常，不能把日志悄悄吞掉当作成功。

第二条验证不能再改级别。若这里漏了检查，释放后的对象仍接受配置变化，就会破坏“生命周期已结束”的约定。

第三条验证不能重新打开文件。它在创建目录或 logger 之前就应被生命周期检查拒绝，不能让 Dispose 后的对象重新复活。

最后重复 `Dispose` 没有 Expect 包装，意味着它应正常返回；任何异常都会直接令检查失败。实现通过 `if (disposed) { return; }` 保证正常重复释放安全。

这些检查使用的旧实例没有打开过文件，因此这里验证的是外层对象的生命周期入口，不是模拟文件句柄的并发释放或磁盘错误。

## 21. 当前实例仍按自己的 INFO 门槛工作

```csharp
WriteSamples(current, process);
CheckRows(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
    ServerLogLevel.Info, process.Role);
```

只向 current 写样本，预期仍是 INFO、ERROR 两条。若释放 previous 导致 current 也被视为已释放，写入会抛异常；若实例之间共享并污染了级别，数量或级别顺序可能不符。

没有调用 `current.OpenServer`，所以它走 stdout，`instance` 仍为 Login。这个场景把级别隔离与释放隔离放在同一个可观察结果里，但没有验证所有可能的两个实例并发交错。

离开 try 块时，`using var current` 释放当前实例；随后 `finally` 恢复该辅助方法进入前的 stdout。外层方法继续执行自己的总清理。

## 22. 外层 finally 恢复全局环境，再删除临时文件

```csharp
finally
{
    Console.SetOut(originalOutput);
    Console.SetError(originalError);
    Environment.SetEnvironmentVariable(EngineEnvironment.LogRoot, originalRoot);
    Directory.Delete(root, recursive: true);
}
```

这段在测试步骤正常返回或抛异常时都会尝试执行。先恢复标准流，后恢复环境变量，再删除测试根目录及子目录。原环境变量不存在时保存的是 `null`，恢复为 `null` 会移除测试临时设置。

各层 `using` 会在控制流离开其作用域时清理 logger 和 writer，因此外层删除目录时，正常路径下文件 logger 已结束使用。这不是“删除目录会替你正确关闭 logger”。

清理本身也可能失败，例如文件仍被占用时删除可能抛异常；代码没有将这种失败伪装为成功。看到主体断言走完，不代表可以忽略清理结果。

最后的 PASS 文本位于整个 `try/finally` 之后，使用已恢复的标准输出。只有前面检查和清理都正常结束，才会走到这句提示。

## 23. 本文件实际证明到哪里

这些检查覆盖：合法/缺省/非法日志配置，三种门槛过滤，JSON 的一组选定字段，文件前后的实例字段，普通日志去向，显式 stderr 镜像，重复打开拒绝，释放后拒绝使用，重复释放和两个对象之间的隔离。

它们不启动真实服务、不申请 ZooKeeper 身份，也不专门覆盖小时/大小滚动、磁盘耗尽、所有 JSON 可选字段、并发写入和释放竞争，或运行中不断改变级别的所有时序。

文中的“应该通过”来自源码执行顺序和断言含义，属于阅读解释；本轮文档重写没有实际运行测试。可回查 [GameLog.cs](../../server/engine/GameLog.cs.md)、[LogEventJson.cs](../../server/engine/logging/LogEventJson.cs.md) 和 [ServerLogLevel.cs](../../server/engine/logging/ServerLogLevel.cs.md) 对照每一个预期的来源。
