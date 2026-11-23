# ConsoleSession.cs 代码详解

[源码](../../../../tools/server-console/ConsoleSession.cs)

## 会话管理什么

`Program` 为当前仓库创建一个 `ConsoleSession`，传给 `MainWindow`。窗口把启动、关闭、编译和导出命令交给会话。会话管理整组进程和启动资料，每个 `ServerInstance` 管理一个槽位的进程句柄、实际身份和日志。 [Program 详解](Program.cs.md) · [MainWindow 详解](MainWindow.xaml.cs.md) · [ServerInstance 详解](ServerInstance.cs.md)

| 成员 | 含义 |
| --- | --- |
| `root` | 仓库绝对路径，也是子进程的工作目录 |
| `ProjectSettings` | 从 `tools/server-console/project.json` 加载的环境显示名和菜单命令列表 |
| `Instances` | 当前启动计划对应的槽位集合。公开类型是 `IReadOnlyList<ServerInstance>`，窗口可枚举但不能通过该接口修改集合 |
| `sessions`、`logRoot` | 前者为 `logs/tools/server-console`，保存会话指针和每轮运行记录。后者为 `logs/server`，存放实例日志 |
| `sessionName` | 当前运行组的目录名，创建第一轮前可能为 `null` |
| `busy` | 是否有命令尚未完成 |
| `Output`、`InstancesChanged` | C# 事件。前者传输出文本，后者通知窗口实例集合需要重绑。 `?.Invoke(...)` 有订阅者时才调用 |

`current` 是包含目录名的文本文件。假设其内容为 `20261124-050000-0000000`，会话记录放在 `logs/tools/server-console/20261124-050000-0000000/`，`Snapshot(name)` 返回可执行文件和依赖所在的 `build/server-console/runs/20261124-050000-0000000/`。源配置路径由 `SelectedConfigurationPath` 单独解析。空白环境变量回退到 `code/server/config/server.yaml`，相对路径以仓库根目录为基准。

## 构造与重开控制台

1. 构造函数先标准化仓库路径并加载控制台项目设置。若 `current` 不存在，`LoadSelectedConfiguration().Group` 解析选中的源 YAML。 `CreateInstances` 用 LINQ 的 `Select` 为 `Group.Processes` 的每项创建一个 `ServerInstance(process.Role, process.ChannelId)`，再以 `ToArray` 固定这一组槽位。
2. 若 `current` 已存在，去掉首尾空白后验证非空且只含 ASCII 字母、数字和连字符。随后检查目标目录的 `console` 与 `dynamic-discovery` 标记。无效指针或旧版运行组会抛 `InvalidDataException`，阻止窗口按错误身份恢复。
3. 恢复分支同样加载当前选中的源 YAML，创建槽位后以 `Open(saved)` 设置 `sessionName`。该方法把运行目录、日志根和二进制快照路径交给每个槽位，槽位依次尝试附加原进程。窗口后续调用的 `Refresh` 也只是遍历槽位，把状态刷新交给实例。

配置按当前源文件加载。若运行组已启动，再修改源配置或改变所选路径，重开的槽位计划可能与旧运行组不一致。会话没有对整个运行组执行配置迁移。

## 各个菜单命令

| 入口 | 执行路径 |
| --- | --- |
| `StartAsync` | 用 `Where(i => !i.Alive)` 找出缺失槽位。没有缺失时报告已启动。部分缺失时在现有会话和二进制快照补起。全部缺失时先 `Compile`，再 `StartNewRun` |
| `StopAsync(selected)` | 取 `Instances.Intersect(selected)`，只把当前集合里的被选槽位交给 `ServerInstance.StopAsync`，用 `Task.WhenAll` 等各自停止 |
| `BuildAsync(false)` | 只编译服务器项目 |
| `BuildAsync(true)` | 编译成功后更换整轮运行组 |
| `ExportAsync` | 先对 Engine、再对 Shared 执行 `dotnet msbuild ... -restore -target:Protobuf_Compile`，分别生成基础协议和业务协议代码 |

每个命令包在 `Command(Func<Task> action)` 里。 `Func<Task>` 是可调用的对象，调用后返回代表操作进度的任务。重入时抛出“上一项操作仍在进行”。 `await action()` 把等待交回 WPF 的事件循环，`finally` 在完成或失败后恢复 `busy`。这里依赖 UI 线程调度命令、周期刷新和异步续体。 `Task.WhenAll` 能同时等待多个进程退出，调用方等待的是整个任务集合，窗口仍可继续更新。

例如同组的 `Router1` 槽位失去进程，其他槽位仍活着，再点启动时会沿现有快照补起 `Router1Server.exe`。 `StartInfo` 会重新取得源配置路径，补起的服务器自己读取该源文件。如果服务器认领到 `Router7`，控制台之后显示的实际实例名就是 `Router7`。 [槽位与实例身份详解](ServerInstance.cs.md)

## `StartNewRun` 的切换顺序

1. 重新加载选中的 YAML，取 `Group.Processes` 的名称序列。按顺序与当前 `SlotName` 做 `SequenceEqual`，序列变了才创建新的实例集合。实际认领的 `Name` 即使变化也不会触发重建。
2. 用 UTC 时间和固定格式 `yyyyMMdd-HHmmss-fffffff` 生成新组名。在 `Task.Run` 中从 `build/bin/TGame.Server/debug` 准备新组二进制文件。先复制是为了让旧组继续运行到新组启动资料已就绪。
3. `PrepareSnapshot` 递归遍历输出文件，保持相对路径复制，跳过相对路径以 `config/`（系统目录分隔符）开头的整个目录。最后将 `TGame.Server.exe` 复制为每个槽位独立的 apphost，例如 `Router1Server.exe`。apphost 是启动同一套 .NET 应用的可执行入口，独立文件名供进程扫描使用。
4. 建本轮运行目录，写 `console` 和 `dynamic-discovery` 标记，生成全部 `ProcessStartInfo`。至此旧进程仍可运行。随后用 `Task.WhenAll` 等旧槽位停止。计划变化时释放旧槽位的句柄、替换集合。
5. 先把新组名写到 `current.tmp`，再用 `File.Move(..., true)` 替换 `current`。 `Open(name)` 将槽位切到新运行资料。若计划变化，触发 `InstancesChanged` 让窗口更新页签。随后按实例顺序逐个调用 `Launch(instance, start)`。
6. 所有 `Launch` 返回后尝试清理上一组的二进制快照。I/O 或权限异常只写提示，因为新组此时已经启动。

编译、复制快照、创建运行标记或生成启动参数失败时，停止旧组的步骤还未执行。停止、替换指针或逐个启动期间出错则由调用方处理。已完成的磁盘动作或启动动作会留在现场，方法没有跨整轮的回滚。启动调用返回表示系统已经创建子进程，就绪由后续日志刷新确认。仅有部分进程启动成功时，`StartAsync` 有补起路径。

## 子进程参数和输出

`StartInfo` 从本轮二进制快照选取槽位 apphost，并在 `ArgumentList` 加一个类型参数，例如 `Router`。 `ArgumentList` 按参数单元传值，调用方不必拼接一整条带引号的命令行。它验证所选源配置文件存在，再设置三项子进程环境变量。

| 环境键 | 值 |
| --- | --- |
| `EngineEnvironment.Config` | 选中的源 YAML 绝对路径 |
| `EngineEnvironment.RunDirectory` | 本轮运行目录，用于身份和停止标记 |
| `EngineEnvironment.LogRoot` | 实例日志根目录 |

`NewProcess` 给服务器和 `dotnet` 子进程统一设置仓库工作目录，禁用 Shell 启动并避免创建新控制台窗口。它从子进程将要继承的环境中移除代码列出的历史 Mongo、Redis 密码变量。它不修改父进程环境。会话只复制二进制输出，配置由 App 自己加载源文件及 include。新建会话、重开或补起均不保存配置快照，也不改写 YAML。

`Dotnet` 优先使用 Program Files 下的 `dotnet.exe`，不存在时用 `dotnet` 交给系统查找。 `RunProcess` 重定向标准输出与标准错误，同时等待两个 `Forward` 和进程退出，避免只读取一条管道时另一条缓冲区堆满。 `Forward` 用 `while (await reader.ReadLineAsync() is { } line)` 将每个非空引用的读行结果交给 `Report`，读到流末尾的 `null` 才停止。退出码非零会抛异常，成功才发成功消息。 `using var child` 在方法退出时释放该 `Process` 包装对象。

会话的 `Dispose` 同样只释放实例持有的进程句柄。正常关闭窗口后服务器继续运行，下一次启动控制台可读取磁盘记录重新附加。停止服务器走单独的 `StopAsync`。
