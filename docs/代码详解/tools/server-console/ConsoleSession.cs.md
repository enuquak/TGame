# ConsoleSession.cs 代码详解

[源码](../../../../tools/server-console/ConsoleSession.cs)

`ConsoleSession` 管理当前仓库的一组服务器进程：从配置建立启动槽位，编译服务器，为本轮复制可执行文件，启动或停止进程，并让重开的控制台重新找到它们。窗口只发起命令和显示状态；每个槽位的 PID、日志、实际实例身份由 `ServerInstance` 管理。

调用主线是 [Program](Program.cs.md) 创建会话并交给 [MainWindow](MainWindow.xaml.cs.md)，窗口调用本文件的命令，命令再调用 [ServerInstance](ServerInstance.cs.md)。这里的“会话”指控制台一次运行组的磁盘资料，与引擎的 ZooKeeper 会话不是同一对象。

## 1. 先认清三个目录和两种名称

```csharp
private readonly string root, sessions, logRoot;
private string? sessionName;
private bool busy;
```

`readonly` 表示这些字段在构造完成后不再重新赋值。`string?` 表示 `sessionName` 可以为 null：首次打开控制台但尚未创建运行组时，没有当前轮次名称。

| 值 | 对应内容 |
| --- | --- |
| `root` | 仓库绝对路径，也是子进程工作目录 |
| `sessions` | `logs/tools/server-console`，保存 current 指针和各轮运行资料 |
| `logRoot` | `logs/server`，保存各实际实例的服务器日志 |
| `sessionName` | 某轮目录名，如 `20261125-010203-1234567` |
| `Snapshot(name)` | `build/server-console/runs/<name>`，保存该轮可执行文件及依赖副本 |

`Snapshot` 这个方法名指构建输出副本。当前实现跳过 `config` 目录，并通过环境变量直接把源 YAML 路径传给服务器；没有保存运行配置快照，也没有写回源配置。

一个 `ServerInstance` 有控制台稳定的 `SlotName`，还有服务器实际认领后显示的 `Name`。例如槽位 `Router1` 启动 `Router1Server.exe`，服务器可能认领到 `Router7`。本文件用槽位管理启动计划，实际身份由引擎认领后回报。

## 2. 属性和事件怎样暴露给窗口

```csharp
public IReadOnlyList<ServerInstance> Instances
{
    get; private set;
}

public event Action<string>? Output;
public event Action? InstancesChanged;
```

`IReadOnlyList<ServerInstance>` 是只读列表接口：窗口可以读取数量、按索引取槽位或枚举，不能通过这个接口增删项。它不意味着里面的 `ServerInstance` 对象不可变化。

`get; private set;` 是自动属性，外部可读，本类内部可以替换整个列表。只有新配置的槽位名称序列变化时，后面的代码才会用新列表替换它。

`Action<string>` 是接收字符串、不返回值的委托类型，`Action` 则不接收参数。加上 `event` 后，窗口通过 `+=` 订阅、`-=` 退订，事件由本类触发。

```csharp
private void Report(string text) => Output?.Invoke(text);
```

`=>` 是表达式体方法；`?.Invoke` 表示事件有订阅者才调用。触发是当前调用过程的一部分，不会自动排队到后台线程，也不自动屏蔽订阅函数的异常。窗口订阅的是把文本放入队列的方法，后续再批量显示。

## 3. 构造函数：首次打开和恢复旧轮次分开处理

```csharp
root = Path.GetFullPath(repoRoot);
ProjectSettings = ConsoleProject.Load(Path.Combine(root, "tools", "server-console", "project.json"));
sessions = Path.Combine(root, "logs", "tools", "server-console");
logRoot = Path.Combine(root, "logs", "server");
var current = Path.Combine(sessions, "current");
if (!File.Exists(current))
{
    var configured = LoadSelectedConfiguration().Group;
    Instances = CreateInstances(configured);
    return;
}
```

先标准化仓库路径，再加载控制台自身的 `project.json`。这里的菜单配置与服务器 `server.yaml` 是两类不同配置。

没有 `current` 文件时，仅按服务器配置创建槽位并返回，不会立刻编译或启动服务器。构造对象与发起进程启动是两个动作。

有指针时，继续读取并校验：

```csharp
var saved = File.ReadAllText(current).Trim();
bool validDirectoryName = saved.Length > 0 && saved.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
if (!validDirectoryName || !File.Exists(Path.Combine(sessions, saved, SessionMarker)))
{
    throw new InvalidDataException("控制台当前会话记录无效。");
}
```

`All(c => ...)` 对每个字符检查条件；lambda 参数 `c` 是当前字符。只接受 ASCII 字母、数字和连字符，且不能空。这样保存值只能充当有限字符集的目录名，不能用斜杠、点路径等把后续组合路径指向任意位置。

还要求目标目录中有 `SessionMarker`，其值为 `console`。随后单独检查 `ServerRunFiles.DynamicDiscoveryMarker`；缺少时抛出旧运行组不兼容的说明，而不是按新身份规则盲目恢复。

最后重新加载当前选中的源配置，创建槽位并 `Open(saved)`。这不是从过去轮次读配置快照：修改源 YAML 后重开窗口，当前槽位计划可能与旧组不同；这里没有自动迁移旧组配置一致性的机制。

## 4. CreateInstances 与 Refresh：创建对象和刷新对象

```csharp
private static ServerInstance[] CreateInstances(ServerGroup group) =>
    group.Processes.Select(process => new ServerInstance(process.Role, process.ChannelId)).ToArray();
```

`Select` 为配置中的每项创建一个槽位对象。lambda 只在序列被枚举时执行；末尾 `ToArray()` 枚举并固定成数组，所以这个方法返回时槽位已经创建完毕。

`process.Role` 是进程类型文本，`ChannelId` 是计划编号，实例名由 `ServerProcess.FormatName` 在槽位内部统一生成。没有给唯一实例省略编号的分支。

```csharp
public void Refresh()
{
    foreach (var instance in Instances)
    {
        instance.Refresh();
    }
}
```

刷新按顺序调用每个槽位，没有启动新线程。它在窗口的计时器回调中运行。某个槽位刷新抛错会结束这次遍历，由窗口的错误处理接住，后面的槽位要等后续刷新机会。

## 5. Command 为什么接收一个函数

```csharp
private async Task Command(Func<Task> action)
{
    if (busy)
    {
        throw new InvalidOperationException("上一项操作仍在进行。");
    }

    busy = true;
    try
    {
        await action();
    }
    finally
    {
        busy = false;
    }
}
```

`Func<Task>` 表示一个无参数、调用后返回 `Task` 的函数。命令方法把“具体做什么”封装成委托交过来，`Command` 负责共有的重入保护和恢复状态。

保存委托不会执行它；这里的 `action()` 才真正调用。`await` 观察任务成功、失败或取消，直到命令结束。`finally` 保证即使它抛错，`busy` 也恢复为 false；异常仍向外传播。

`busy` 不是线程锁。源码注释明确依赖窗口命令、计时器和异步续体在同一 UI 线程调度。界面在 await 期间可继续处理消息，但另一条命令会看到 busy。若将来从任意工作线程并发调用这些方法，不能由这个 bool 推导线程安全。

本文件没有给命令接收取消令牌，也没有“取消编译”的按钮回调。不要把异步等待误读成已经支持用户取消。

## 6. StartAsync 的三个分支必须分别看

```csharp
public Task StartAsync() => Command(async () =>
{
    var missing = Instances.Where(i => !i.Alive).ToArray();
    if (missing.Length == 0)
    {
        Report("所有进程均已启动。");
        return;
    }

    if (missing.Length != Instances.Count)
    {
        Launch(missing, sessionName!);
        return;
    }

    await Compile();
    await StartNewRun();
});
```

外层 `StartAsync` 自身没有 `async`，它直接返回 `Command` 的任务；真正异步工作在传入的 `async () => { ... }` 中。无参数 lambda 的 `()` 不能省略成一个普通代码块。

`Where(i => !i.Alive)` 筛出没有活进程的槽位，`ToArray` 固定本次缺失列表。之后三条路径互斥：

1. 没有缺失：只输出提示，不编译、不启动。
2. 部分缺失：在现有轮次和构建副本上补起，保留其余活进程。
3. 全部缺失：先编译，成功后创建新轮次并启动。

`sessionName!` 的 `!` 是 null 容忍运算符，告诉编译器此处按非空分析，不会在运行时生成一个默认名称或额外检查。代码依赖“部分槽位已活着时已经存在当前轮次”的会话状态。

例如三个槽位中只有 `Router1` 退出，重新点击启动只会补起该槽位。它使用当前轮次的二进制副本，但 `StartInfo` 会重新取得源配置路径，服务器新进程自己重新加载 YAML。没有把旧进程内存配置复制给新进程。

## 7. Stop、Build、Export 仍通过同一个命令入口

```csharp
public Task StopAsync(IReadOnlyList<ServerInstance> selected) => Command(() =>
    Task.WhenAll(Instances.Intersect(selected).Select(i => i.StopAsync(Report))));
```

`Intersect` 取当前槽位集合与被选集合的交集，避免对不属于当前列表的对象执行停止。当前 `ServerInstance` 没有自定义值相等，实际选中的应是同一批对象引用。

`Select` 产生停止任务，`Task.WhenAll` 枚举并等待全部任务结束。它允许多个进程的退出等待重叠，不表示为每个进程创建一个专用线程。空选择产生空任务集合，可正常完成。某个停止失败会让等待的命令失败，不会假装整组都停止成功。

```csharp
public Task BuildAsync(bool restart) => Command(async () =>
{
    await Compile();
    if (restart)
    {
        await StartNewRun();
    }
});
```

`false` 只编译，`true` 在编译成功后切换新轮次。编译失败时 `StartNewRun` 不执行，现有进程不会因这个方法主动停止。

`ExportAsync` 依次执行 Engine 和 Shared 项目的 `dotnet msbuild ... -target:Protobuf_Compile`，第一个成功后才执行第二个。这里导出的是协议生成代码，不是导出用户日志。`Compile` 则把 `dotnet build` 的项目路径和参数交给共有进程执行函数。

## 8. StartNewRun 先确定槽位计划是否变化

```csharp
var configuration = LoadSelectedConfiguration();
var group = configuration.Group;
var configuredNames = group.Processes.Select(process => process.Name);
bool changed = !Instances.Select(instance => instance.SlotName).SequenceEqual(configuredNames);
var nextInstances = Instances;
if (changed)
{
    nextInstances = CreateInstances(group);
}
```

这次重新加载选中的源 YAML，取 `Group.Processes` 名称序列。`SequenceEqual` 同时比较数量、顺序和对应名称，不是集合无序比较。

比较对象是 `SlotName`，不是服务器认领后的 `Name`。槽位 `Router1` 显示成 `Router7` 不代表启动计划变化。计划不变就复用槽位对象，后面通过 `Open`/`Reset` 切换其运行状态；计划变了才创建新数组。

`nextInstances = Instances` 只是把同一列表引用赋给局部变量，不是复制所有对象。

## 9. 在旧进程仍运行时准备新轮次

```csharp
var name = DateTime.UtcNow.ToString(SessionDirectoryFormat, CultureInfo.InvariantCulture);
var snapshot = Snapshot(name);
var runDir = Path.Combine(sessions, name);
// 在停止健康进程前准备完整的启动信息和构建快照。
await Task.Run(() =>
{
    var source = Path.Combine(root, "build", "bin", "TGame.Server", "debug");
    var apphosts = nextInstances.Select(instance => Path.Combine(snapshot, instance.ExecutableName));
    PrepareSnapshot(source, snapshot, apphosts);
});
```

目录名用 UTC 时间和固定格式 `yyyyMMdd-HHmmss-fffffff`，`InvariantCulture` 避免格式受用户地区设置改变。这个字符串用于区分轮次，不是分布式 ID 分配器。

这里明确使用 `Task.Run` 把同步文件复制放到线程池工作中，减少复制期间阻塞 UI 的时间。它与普通 `await` 不同：只有这一段显式安排了后台工作。lambda 捕获 `root`、`snapshot` 和 `nextInstances` 等外部值，供工作函数使用。

等待结束后，正常窗口调用上下文会把续体调回 UI 调度。后面的目录创建、写标记等仍是同步文件操作，并没有因为整个方法带 `async` 就全部变成后台 I/O。

```csharp
Directory.CreateDirectory(runDir);
File.WriteAllText(Path.Combine(runDir, SessionMarker), "");
File.WriteAllText(Path.Combine(runDir, ServerRunFiles.DynamicDiscoveryMarker), "");
var starts = nextInstances.Select(i => StartInfo(i, name)).ToArray();
await Task.WhenAll(Instances.Select(i => i.StopAsync(Report)));
```

先创建运行资料、写两个空标记、构造所有启动参数，再停止旧组。`ToArray` 保证所有 `StartInfo` 都在停止前被实际执行，而不只是保存一个延迟查询。

所以编译、复制、检查配置文件存在或准备启动资料时失败，旧健康进程还没有被这个方法停止。它并不保证“新进程一定能成功启动”；磁盘副本和参数准备成功只是提前排除一部分错误。

## 10. 切换列表、写指针、启动和清理有固定顺序

```csharp
if (changed)
{
    foreach (var instance in Instances)
    {
        instance.Dispose();
    }

    Instances = nextInstances;
}

var previous = sessionName;
var current = Path.Combine(sessions, "current");
File.WriteAllText(current + ".tmp", name);
File.Move(current + ".tmp", current, true);
Open(name);
if (changed)
{
    InstancesChanged?.Invoke();
}
```

旧组停止完成后，计划变化时释放旧槽位持有的进程句柄，替换列表。`previous` 保存旧轮次名称，供最后清理其二进制副本。

新指针先写入 `current.tmp`，再用带覆盖参数的 `File.Move` 替换 `current`，减少直接逐字写正式指针的窗口。这里没有全组事务或磁盘持久性保证；后续步骤失败不会把已写指针自动回滚。

`Open(name)` 更新会话当前名称，并把本轮目录交给每个槽位。只有列表变化才触发 `InstancesChanged`，窗口同步重建页签。

```csharp
for (int i = 0; i < Instances.Count; i++)
{
    Launch(Instances[i], starts[i]);
}
```

按槽位索引逐个启动，启动信息与槽位数组一一对应。`Process.Start` 返回表示系统已创建进程，不表示引擎依赖连接完成或已经发布地址；真正就绪要等日志里的 `PROCESS_READY` 等事件。

所有启动调用返回后，才尝试删除 `previous` 的构建副本。仅 I/O 和权限异常被捕获并报告“旧构建快照待清理”。这些清理错误不撤销已经启动的新组。

停止旧组、改指针或逐个启动中途失败时，没有回滚整个过程。可能留下部分已启动新进程或准备好的目录；已有部分进程存活时，`StartAsync` 提供补起路径。这是现有恢复策略的边界。

## 11. PrepareSnapshot 复制什么、跳过什么

```csharp
foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(source, file);
    if (relative.StartsWith("config" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    string dest = Path.Combine(snapshot, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    File.Copy(file, dest);
}
```

递归枚举构建输出文件，按相对路径保留子目录结构。相对路径以 `config` 加当前系统目录分隔符开头就 `continue`，跳过该文件。字符串比较忽略大小写，适合这里的 Windows 目录环境。

`Path.GetDirectoryName(dest)!` 中的 `!` 只压制可空分析；代码依赖 `dest` 是由完整快照根与相对文件路径组合而成，因此应有父目录。`CreateDirectory` 可用于父目录尚不存在的情况。

`File.Copy(file, dest)` 未传覆盖参数，已有同名目标或访问失败可以抛异常。整个方法没有吞掉这些错误。

```csharp
foreach (var apphost in apphosts)
{
    File.Copy(Path.Combine(snapshot, "TGame.Server.exe"), apphost);
}
```

为每个槽位复制独立命名的 apphost，如 `Router1Server.exe`。apphost 是本机可执行入口，背后仍启动同一套服务器程序集；独立名字让恢复代码能先按进程名称缩小候选范围，再验证完整路径。

“构建副本”不只是 exe，也包括复制到的程序集和其他依赖；但明确不包括 `config/` 下文件。源配置由下一节单独传入。

## 12. 配置路径和 Open 的职责

```csharp
private ServerConfiguration LoadSelectedConfiguration() => new(SelectedConfigurationPath());
```

`new(...)` 是目标类型推断的构造表达式，这里返回类型明确是 `ServerConfiguration`，因此省略了构造表达式中的类型名。

`SelectedConfigurationPath` 先读 `EngineEnvironment.Config` 环境变量；null、空串或全空白时使用 `ServerConfiguration.ConfigurationPath`。`Path.GetFullPath(selected, root)` 把相对路径解释为相对于仓库根目录，不依赖窗口当时的进程工作目录。

`Open(name)` 保存 `sessionName`，然后逐个调用 `instance.Open(运行目录, 日志根, 构建副本路径)`。它不编译、不创建服务器进程；槽位根据磁盘记录尝试恢复已有进程。

补起用的 `Launch(ServerInstance[] missing, string name)` 也先 `ToArray` 构造所有缺失槽位的 `StartInfo`，然后逐个调用另一个同名重载。C# 通过参数类型区分这两个 `Launch` 方法。

## 13. StartInfo 只传类型、源配置和运行目录

```csharp
var info = NewProcess(Path.Combine(Snapshot(name), instance.ExecutableName));
info.ArgumentList.Add(instance.Role);
var config = SelectedConfigurationPath();
if (!File.Exists(config))
{
    throw new FileNotFoundException("服务器配置不存在。", config);
}

info.Environment[EngineEnvironment.Config] = config;
info.Environment[EngineEnvironment.RunDirectory] = Path.Combine(sessions, name);
info.Environment[EngineEnvironment.LogRoot] = logRoot;
return info;
```

`ProcessStartInfo` 是“怎样启动”的参数对象，此时尚未创建进程。`ArgumentList` 按一个参数一个元素加入 `Router` 这样的类型名，避免自己拼接整条命令行的引号。

传的是类型 `Role`，不是预先强制分配的实际实例名。服务器通过 ZooKeeper 临时占号取得实例身份，再向运行目录写身份标记。

环境变量分别指向源 YAML、本轮运行目录和日志根。没有生成另一个 YAML，没有把 `RunDirectory` 写回配置。服务器 App 启动时加载源配置及 include，运行期间按内存快照使用，不自动重载。

`NewProcess` 设置仓库工作目录、`UseShellExecute = false`、`CreateNoWindow = true`。它还移除代码列出的历史 Mongo/Redis 密码环境键，包括测试键；修改的是将交给子进程的环境集合，不是父进程环境。不能把这一小组键移除描述成对所有任意凭据的全面过滤。

## 14. Dotnet、RunProcess、Forward 如何避免漏读输出

`Dotnet(string operation, params string[] arguments)` 中的 `params` 允许调用方把多个字符串直接列为实参。方法优先选择 Program Files 下的 `dotnet.exe`，文件不存在则使用 `dotnet` 名称交给系统查找，再逐项加入 `ArgumentList`。

```csharp
info.RedirectStandardOutput = info.RedirectStandardError = true;
Report(operation + "…");
using var child = Process.Start(info) ?? throw new InvalidOperationException(operation + "启动失败。");
await Task.WhenAll(Forward(child.StandardOutput), Forward(child.StandardError), child.WaitForExitAsync());
```

链式赋值把两个重定向属性都设为 true。`?? throw` 处理进程启动 API 返回 null 的情况。`using var child` 保证离开方法作用域时释放 `Process` 包装对象，成功和异常退出都会走释放。

三个任务一起等待：持续读标准输出、持续读标准错误、等待进程退出。两个管道都及时消费，避免只等退出而让子进程因输出管道积压停住。`Task.WhenAll` 仍是等待组合，不是自动把所有函数改为独立线程。

```csharp
while (await reader.ReadLineAsync() is { } line)
{
    Report(line);
}
```

`is { } line` 匹配非 null 的读行结果并绑定变量。空字符串表示空行，仍会报告；null 才表示流结束，停止循环。每条记录触发 `Output`，窗口只是入队，因此输出显示可以稍后批量进行。

等待全部结束后检查 `child.ExitCode`，非零抛出带退出码的异常，只有零才报告成功。这里没有取消参数或超时，也没有在读取异常时主动杀死编译子进程的额外 finally。

## 15. 关闭控制台不等于停止服务器

```csharp
public void Dispose()
{
    foreach (var instance in Instances)
    {
        instance.Dispose();
    }
}
```

`Dispose` 释放槽位持有的系统进程包装对象，不调用 `StopAsync`，不删除运行资料，也不强制退出服务器。`Program` 用 `using var session` 管理它，所以窗口退出后服务器可以继续运行，下次重开依靠 current、槽位身份 JSON、启动标记和实际身份标记恢复。

停止服务器是明确的命令流程：窗口选择槽位 → `ConsoleSession.StopAsync` → `ServerInstance.StopAsync` 写精确停止标记并等待退出。进一步的 PID 与时间校验、日志追平和强制终止边界，继续读 [ServerInstance 详解](ServerInstance.cs.md)。
