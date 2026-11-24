# ServerInstance.cs 代码详解

[源码](../../../../tools/server-console/ServerInstance.cs)

一个 `ServerInstance` 对应控制台启动计划中的一个槽位。它持有系统进程包装对象，记录一次启动的 PID 和时间，恢复已有进程，追读该进程的日志，再把这些证据转换成界面状态。它不是引擎里的进程身份对象，也不负责 ZooKeeper 占号。

[ConsoleSession](ConsoleSession.cs.md) 创建和管理槽位；本文件把日志原文交给 [RollingLog](RollingLog.cs.md) 读取，再交给窗口的页面展示。阅读时要分清“槽位一直存在”和“槽位里这一轮系统进程已经更换”。

## 1. 主构造函数、属性和默认编号

```csharp
internal sealed class ServerInstance(string role, int channelId = 1) : IDisposable
```

类名后的参数是 C# 主构造函数。创建 `new ServerInstance("Router", 1)` 时，参数可以直接供类中属性初始化器使用。`channelId = 1` 是省略参数时的默认值，不表示名称可以省略数字。

`internal` 限定程序集内使用，`sealed` 禁止继承，`IDisposable` 表示提供同步释放方法。

```csharp
public string Role { get; } = role;
public string Name { get; private set; } = ServerProcess.FormatName(role, channelId);
internal string SlotName { get; } = ServerProcess.FormatName(role, channelId);
```

`Role` 是启动类型，如 `Router`。`SlotName` 是稳定的控制台槽位名，如 `Router1`。`Name` 初始相同，但服务器注册后可能变成 `Router7`。`get; private set;` 让外部可读而只能由本类更新。

三者不是同一事实的无意义复制：类型固定，槽位用于找到启动资料，实际身份由服务端认领。`ServerProcess.FormatName` 统一保证“类型名 + 从 1 开始编号”的规则。

## 2. 文件名必须知道用了哪个身份

```csharp
internal string ExecutableName => SlotName + "Server.exe";
private string IdentityPath => Path.Combine(runDir, SlotName + ".json");
private string LaunchMarker => Path.Combine(runDir, "starting-" + SlotName);
```

这些是计算属性，读取时根据已有字段计算，没有另存一套路径。槽位 `Router1` 的可执行入口和记录始终是 `Router1Server.exe`、`Router1.json`、`starting-Router1`。

实际身份改为 `Router7` 后，服务器日志目录改为 `Router7Server`，但槽位文件名不跟着改。否则下次重开控制台就无法按原始计划找到这个槽位的记录。

运行目录还有服务端写入的 `instance-<PID>-<ticks>` 和控制台写入的 `stop-process-<PID>-<ticks>`。前者交接实际身份，后者请求精确停止一次进程生命期。前缀和键格式来自引擎 `ServerRunFiles`，本文件不另造一套。

## 3. 进程引用、生命周期资料和事件状态

```csharp
private sealed record Identity(int Pid, long StartedUtcTicks);
private readonly List<string> lines = [];
private Process? process;
private string runDir = "";
private string logDir = "";
private string logRoot = "";
private bool identityResolved;
private long startedUtcTicks;
private RollingLog? log;
private bool initializing, initialized, connected, failed;
```

`Process?`、`RollingLog?` 允许字段为空。创建槽位时还没有系统进程，也没有可读的这一轮日志。

`record Identity(...)` 声明保存两个值的记录类型。比较 `actual != saved` 时按成员值比较，而不是只看是不是同一对象引用。PID 加 UTC 启动时间用于区分 PID 被操作系统复用后的另一个进程。

`identityResolved` 表示“已经读取过这次生命期的实际身份”，不是当前连接可用性。四个 bool 根据日志事件记录状态，`Reset` 会统一清空；其中 `connected` 还会随连接事件更新，收到 `LinksLost` 时立即变为 `false`。

```csharp
internal bool Alive => process is { HasExited: false };
```

属性模式同时要求对象非 null 且 `HasExited` 为 false。`Pid` 有值并不推出 `Alive` 为真：旧进程退出后仍保留 PID，方便显示和追读残留日志。

`Lines` 以只读列表接口暴露最近的原始行，`LineCount` 是本生命期累计接收数。二者分开是必要的：显示保留最近 1000 行后，累计行号仍应继续增长。

## 4. Open：先切换运行资料，再尝试恢复

```csharp
Reset();
runDir = directory;
this.logRoot = logRoot;
logDir = Path.Combine(logRoot, Name + "Server");
var exe = Path.Combine(snapshot, ExecutableName);
var saved = File.Exists(IdentityPath)
    ? JsonSerializer.Deserialize<Identity>(File.ReadAllText(IdentityPath)) ?? throw new InvalidDataException(IdentityPath)
    : null;
```

先释放旧句柄、清掉旧日志状态，然后保存新运行目录和日志根。`this.logRoot` 是字段，右边 `logRoot` 是形参。

三元表达式按槽位 JSON 是否存在选路径：存在就读文本并反序列化为 `Identity`；不存在就用 null。若 JSON 内容是 `null`，反序列化返回 null，`?? throw` 拒绝它；JSON 格式损坏也会直接抛出解析异常。

```csharp
Pid = saved?.Pid;
startedUtcTicks = saved?.StartedUtcTicks ?? 0;
bool pending = File.Exists(LaunchMarker);
if (pending || saved != null)
{
    RestoreProcess(exe, saved, pending);
}
```

`saved?.Pid` 在 saved 为空时返回 null；`?? 0` 给时间字段提供“尚无记录”的初值。存在启动中标记或已保存身份，才需要搜索系统进程。

恢复返回后，如果原先有启动标记就删除它。有 PID 时建立该生命期的 `RollingLog`，然后立即 `Refresh()`。只有旧 JSON、找不到活进程时，PID 仍被保留，所以还能读旧日志并显示已退出状态。

## 5. RestoreProcess：候选名只是第一层筛选

```csharp
foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
```

先按槽位 apphost 的基础文件名找候选。例如 `Router1Server.exe` 去掉扩展名成为 `Router1Server`。同名进程未必属于当前仓库或当前轮次，所以后面还必须验证。

```csharp
if (!pending && candidate.Id != saved!.Pid)
{
    continue;
}

long ticks = candidate.StartTime.ToUniversalTime().Ticks;
if (!pending && ticks != saved!.StartedUtcTicks)
{
    continue;
}

if (!string.Equals(candidate.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
{
    continue;
}
```

没有启动中标记时，要求 PID、启动 UTC ticks、可执行文件完整路径都匹配。`continue` 放弃当前候选并进入下一个。

`pending` 为 true 时跳过旧 PID/时间匹配：控制台可能在 `Process.Start` 成功后、保存 JSON 前被关闭，旧文件可能不存在或仍是上一轮资料。但完整路径仍必须匹配本轮构建副本，不能只凭名字认领进程。

`saved!` 只向编译器说明此路径按非空处理。安全依据来自调用条件：没有 pending 却进入恢复，就应已有 saved；它不是运行时 null 校验。

`candidate.MainModule?.FileName` 读取实际执行文件路径；若没有模块就得到 null，路径比较失败，不接受候选。

## 6. 采用一个候选与释放其他包装对象

```csharp
if (process != null)
{
    throw new InvalidDataException($"{Name} 在同一会话中有多个实例。");
}

process = candidate;
Pid = candidate.Id;
startedUtcTicks = ticks;
var actual = new Identity(candidate.Id, ticks);
if (actual != saved)
{
    Save(actual);
}
```

已经采用一个候选后又出现第二个完整匹配，会抛出异常，不随便挑一个。成功采用时把 `candidate` 保存到字段，并保存修正后的身份记录。

```csharp
catch (InvalidOperationException) when (candidate.HasExited)
{
}
catch (Win32Exception) when (candidate.HasExited)
{
}
finally
{
    if (!ReferenceEquals(candidate, process))
    {
        candidate.Dispose();
    }
}
```

枚举后进程可能刚好退出，读取属性时出现相应异常。`when` 过滤器只有在候选已退出时才忽略，不会把所有 Win32 权限或访问错误都吞掉。

`ReferenceEquals` 比较是否是同一个包装对象。没被采用的候选在 `finally` 释放；已保存为字段的那个留待槽位以后释放。`finally` 在 continue、成功或异常离开这次 try 时都会执行。

释放 `Process` 包装对象不是终止操作系统进程。它结束的是当前程序持有的相关资源，真正停止在后面的 `StopAsync` 中进行。

## 7. Launch 为什么先写 starting 标记

```csharp
File.WriteAllText(LaunchMarker, "");
Reset();
process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{Name} 启动失败。");
Pid = process.Id;
startedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
log = new RollingLog(logDir, $"{Pid}-{startedUtcTicks}-");
Save(new Identity(process.Id, startedUtcTicks));
File.Delete(LaunchMarker);
Status = ServerStatus.Starting;
```

顺序是：留下启动意图 → 清空上一生命期状态 → 启动子进程 → 读取 PID/时间 → 建日志读取器 → 保存身份 → 删除启动意图 → 显示启动中。

如果窗口在进程已启动而 JSON 尚未保存时结束，starting 标记还能指引 `RestoreProcess` 根据独立 apphost 路径恢复。这不是事务回滚；标记删除或保存失败时，方法会抛错，已经创建的子进程不因此自动终止。

`$"{Pid}-{startedUtcTicks}-"` 是字符串插值，形成本生命期的日志前缀。不能只按角色读所有日志，否则重启前的旧事件可能污染新状态。

此方法返回时只有系统进程已创建。还没有等待 ZooKeeper、ID、监听、依赖连接及地址发布完成，因此状态只能是“启动中”。

## 8. Save 用临时文件替换槽位记录

```csharp
private void Save(Identity identity)
{
    File.WriteAllText(IdentityPath + ".tmp", JsonSerializer.Serialize(identity));
    File.Move(IdentityPath + ".tmp", IdentityPath, true);
}
```

先序列化记录，再写 `.tmp`，最后覆盖正式 JSON。这样正式文件不需要在原位逐字更新。`true` 是允许覆盖现有目标的参数，不是“忽略所有错误”。

这里没有捕获 I/O 异常，没有清理所有可能遗留的临时文件，也没有声称跨文件更新原子。它只为单个身份记录提供先写临时文件再替换的顺序。

## 9. Refresh 先处理实际身份交接

```csharp
if (Pid == null)
{
    return;
}

if (!identityResolved)
{
    var identityPath = Path.Combine(runDir, ServerRunFiles.IdentityPrefix + ServerRunFiles.ProcessKey(Pid.Value, startedUtcTicks));
    if (File.Exists(identityPath))
    {
        var assigned = ServerProcess.Parse(File.ReadAllText(identityPath));
        if (assigned.Role != Role)
        {
            throw new InvalidDataException("Claimed process type does not match its launch type.");
        }

        Name = assigned.Name;
        logDir = Path.Combine(logRoot, Name + "Server");
        log = new RollingLog(logDir, $"{Pid}-{startedUtcTicks}-");
        identityResolved = true;
    }
}
```

没有 PID 就没有这次进程的身份和日志范围，直接返回。`Pid.Value` 是可空整数中的实际值，前面的 null 检查使此处能够读取。

在 App 认领身份并打开实例日志后，它会向运行目录写实际身份标记。控制台解析这个名称，并核对角色与启动类型一致；例如启动 Router 却得到 Gate 名称，不能接纳。

通过检查后切换显示名、日志目录及读取器，最后置 `identityResolved = true`。下一次刷新不再重复读取身份文件。换读取器意味着从实际身份目录的对应前缀开始追读，而不是沿用旧目录偏移。

槽位 JSON 仍记录 `Router1` 槽位的 PID/时间，实际名称可以是 `Router7`。这两个方向的文件分别解决“找回系统进程”和“展示引擎认领身份”。

## 10. 读取日志、处理暂时冲突和裁剪历史

```csharp
bool caughtUp;
try
{
    caughtUp = log?.ReadAvailable(AddLine) ?? true;
}
catch (IOException e) when (e is DirectoryNotFoundException || (e.HResult & HResultCodeMask) is SharingViolation or LockViolation)
{
    // Retry a shard that disappeared or was temporarily locked while the server rolled its log.
    caughtUp = false;
}
```

`AddLine` 是方法组，作为 `Action<string>` 回调交给读取器；每读到一条完整非空行就调用一次。`log` 为 null 时不调用，并通过 `?? true` 把这次视为没有待追内容。

日志滚动期间目录消失、Windows 共享冲突 32、锁冲突 33 被视为可在下一刷新重试。`e.HResult & 0xffff` 用按位与取低 16 位错误码。其他 I/O 错误不匹配过滤器，会交给窗口处理。

`caughtUp` 为 false 也可能只是读取器单次字节预算耗尽，不一定是服务器仍在初始化。因此后面“初始化中”状态有一部分表达的是控制台尚未读完证据。

```csharp
if (lines.Count > MaxRetainedLines)
{
    lines.RemoveRange(0, lines.Count - MaxRetainedLines);
}
```

保留最近 1000 行，删的是列表头部。若本轮累计接收 1150 行，`lines.Count` 变为 1000，`LineCount` 仍为 1150。窗口可用累计数推导最近这些行的绝对编号。

## 11. AddLine：哪些行丢弃，哪些坏行保留

```csharp
using var document = JsonDocument.Parse(line);
var row = document.RootElement;
if (row.GetProperty(ServerLogFields.Role).GetString() != Role ||
    row.GetProperty(ServerLogFields.Pid).GetInt32() != Pid)
{
    return;
}
```

`JsonDocument` 是对 JSON 的解析表示。这里的 `using var` 位于 `try` 块内，离开这个块时就会释放文档，早于后面的 `lines.Add(line)`；提前返回或因异常离开也会释放。`GetProperty` 访问必需字段，缺字段会抛异常；`GetString`/`GetInt32` 要求合适类型。

角色或 PID 不匹配立即返回，整行不会进入列表，也不会增加累计数。`||` 短路：前一个已不匹配，后一个不必再求值。

```csharp
if (row.TryGetProperty(ServerLogFields.Instance, out var instance) && instance.GetString() != Name)
{
    return;
}
```

实例字段是可选的：`TryGetProperty` 返回是否找到，并通过 `out var instance` 带出元素。只有找到且值不匹配时才丢弃。缺失时不因为这一项拒绝。

时间字段也采用可选检查：只有字段存在、是字符串且可解析为 `DateTimeOffset`，才比较 `stamp.UtcTicks < startedUtcTicks`，早于本次启动的记录被丢弃。不能把这段描述成“每行必须有有效时间戳”，源码没有要求这一点。

通过这些筛选后调用 `ApplyEvent`。特定解析、缺字段、类型或格式异常由过滤器捕获，随后仍执行：

```csharp
lines.Add(line);
LineCount++;
```

因此有两类不同处理：确认属于别的进程/旧生命期的记录直接丢弃；结构损坏而无法正常应用事件的原文保留下来供排查。`JsonException` 等被捕获不表示随便把损坏内容当有效状态事件。

## 12. ApplyEvent 记录事件历史，Refresh 再选择显示状态

```csharp
case ServerLogEvents.ProcessReady:
    initialized = true;
    break;
case ServerLogEvents.LinksReady:
    connected = true;
    break;
case ServerLogEvents.LinksLost:
    connected = false;
    break;
```

`switch` 语句按事件名选择分支，`break` 结束当前 switch 分支。其余两项是 `Listen` 把 `initializing` 设真，`ProcessFailed` 把 `failed` 设真。未知事件不改变这些标记，原始日志仍可显示。

这些不是互斥枚举：例如 initialized 与 failed 可以同时为真，最终显示取决于 `Refresh` 的优先级。它按下表顺序命中后立即 return：

| 优先级 | 条件 | 界面状态 |
| --- | --- | --- |
| 1 | 已见 ProcessFailed | 异常退出 |
| 2 | 进程不活着 | 有精确停止标记则已停止，否则异常退出 |
| 3 | 进程活着且有停止标记 | 停止中 |
| 4 | 本轮日志未追平 | 初始化中 |
| 5 | 已见 ProcessReady | connected 真则就绪，否则连接不全 |
| 6 | 尚未完成初始化 | 已见 Listen 则初始化中，否则启动中 |

源码中两个连续分支例如：

```csharp
if (failed)
{
    Status = ServerStatus.Failed;
    return;
}

if (!Alive)
{
    Status = stopping ? ServerStatus.Stopped : ServerStatus.Failed;
    return;
}
```

先判断 failed，意味着即使存在停止标记，已读到的失败事件仍优先显示异常退出。这里不根据系统 ExitCode 来区分正常/异常退出，而是依靠事件、存活和停止标记。

当前引擎在首次连接门槛完成时写 `LINKS_READY`，发布地址后写 `PROCESS_READY`。运行期依赖断开不会撤销引擎 Ready，也没有对应的 `LINKS_LOST` 发出路径；本解析器保留该事件分支。因此“就绪”不能作为所有依赖此刻在线的实时证明。

## 13. StopAsync 先请求优雅退出

```csharp
internal async Task StopAsync(Action<string> report)
{
    if (process is not { HasExited: false } child)
    {
        return;
    }

    var stopMarker = Path.Combine(runDir, ServerRunFiles.StopProcessPrefix + ServerRunFiles.ProcessKey(child.Id, startedUtcTicks));
    File.WriteAllText(stopMarker, "");
    Status = ServerStatus.Stopping;
```

`is not ... child` 是否定属性模式：没有活进程就返回；继续执行时，`child` 绑定到本次要停止的 Process 对象。后面的 await 期间仍使用这个局部引用。

停止标记含 PID 和启动时间，不是宽泛的“停止 Router”。服务端轮询本生命期对应标记后触发取消，按自己的退出链收束任务和资源。

`Action<string> report` 由会话传入 `Report` 方法组，超时需要输出提示时才调用。这里没有硬编码窗口控件依赖。

## 14. 等待超时后才尝试 Kill

```csharp
try
{
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(StopTimeoutSeconds));
}
catch (TimeoutException)
{
    if (!child.HasExited)
    {
        report($"{Name} 超过 {StopTimeoutSeconds} 秒未退出，终止进程 {child.Id}。");
        try
        {
            child.Kill();
        }
        catch (InvalidOperationException) when (child.HasExited)
        {
        }
    }
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(KillTimeoutSeconds));
}
Refresh();
```

第一次等待最多 10 秒。外面的 `WaitAsync(timeout)` 给等待加期限，超时不会自行终止服务器，所以 catch 中才检查并调用 `Kill()`。

检查 `HasExited` 与执行 Kill 之间仍可能发生退出；过滤器只在这种已退出情况下忽略 `InvalidOperationException`。`Kill()` 没有传 `entireProcessTree: true`，不要把它解释成明确杀整棵进程树的实现。

随后再等最多 5 秒。如果这次仍超时，异常继续向上传播，因为当前 catch 不会再次捕获自己内部抛出的异常。标记写入、权限等失败也不在这个 TimeoutException 捕获范围。

等待结束才 `Refresh()`，重新读日志和存活状态。`async` 不把方法放进后台专用线程；正常从 WPF UI 上下文调用时，等待释放 UI 调度机会，续体继续按该上下文执行。

## 15. Reset 与 Dispose 不承担服务器退出

```csharp
private void Reset()
{
    Dispose();
    process = null;
    Pid = null;
    Name = SlotName;
    identityResolved = false;
    logDir = Path.Combine(logRoot, Name + "Server");
    log = null;
    lines.Clear();
    LineCount = 0;
    initializing = initialized = connected = failed = false;
    Status = ServerStatus.NotStarted;
}

public void Dispose() => process?.Dispose();
```

`Reset` 在换轮次或启动前释放包装对象、清 PID、还原显示名、清日志游标和事件状态，供新生命期重新收集证据。链式赋值让四个 bool 都变为 false。

`runDir`、`logRoot` 不是在 Reset 中清空，而由 Open 更新；启动时需要继续使用已打开的轮次路径。`startedUtcTicks` 在 Open 或 Launch 中重新设置，不能仅看 Reset 没写它就把它当成当前身份仍有效，PID 已经被清空。

`Dispose` 只释放 `Process` 包装对象，不调用 Kill，不写停止标记，不删除磁盘记录。因此关闭控制台之后服务器仍可运行，再打开窗口依靠持久记录恢复。反过来，不能拿 Dispose 代替用户的停止命令。

本类没有锁，正常由 UI 线程上的会话命令、计时器和续体使用。它的磁盘恢复逻辑、日志状态推导和超时分支是当前源码行为；本文没有运行进程启动/停止测试来证明所有竞争时序。
