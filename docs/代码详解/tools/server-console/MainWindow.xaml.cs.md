# MainWindow.xaml.cs 代码详解

[源码](../../../../tools/server-console/MainWindow.xaml.cs)

这份文件负责控制台窗口的交互：把槽位绑定成页签，把菜单动作交给会话，每半秒尝试刷新状态，批量显示命令输出，管理实例选择器和快捷键。启动、恢复和停止服务器的实际流程在 [ConsoleSession](ConsoleSession.cs.md) 与 [ServerInstance](ServerInstance.cs.md) 中。

WPF 界面由 [MainWindow.xaml](MainWindow.xaml.md) 描述控件、布局和事件名。本文件处理窗口级行为，日志列表的筛选、定位和跟踪在 [MainWindow.Logs.cs](MainWindow.Logs.cs.md)。阅读时要把这些文件看成同一个窗口的不同部分。

## 1. partial、继承与 InitializeComponent

```csharp
internal partial class MainWindow : Window
```

`: Window` 表示继承 WPF 窗口基类，获得显示、关闭、窗口状态和 Dispatcher 等能力。`partial` 允许同一个类分散到几个源文件；编译时合并为一个类型，不是在运行时通过继承串起来。

因此本文件能直接访问另一部分定义的 `active`，也能调用 `InitializeLogs`、`ResetLogRows`、`UpdateLogs`、`FindMatch`。它们不需要先 `new MainWindowLogs()`。

构造函数中的 `InitializeComponent()` 由 WPF 构建生成，用来实例化 XAML 控件、建立命名字段和连接声明的事件。只有它运行后，后面的 `MachineLabel.Text = ...` 等操作才有控件对象可用。

## 2. 字段各自保存哪一种状态

```csharp
private sealed record ServerCommand(string Name, string Icon, Func<Task>? Execute = null);
private readonly ConsoleSession session;
private readonly UserSettings settings;
private ProcessPage[] pages = [];
private ListCollectionView pickerView = null!;
```

`ServerCommand` 是窗口内部使用的记录类型，保存名称、图标字符和可选执行函数。`Func<Task>?` 表示函数引用可能为 null；缺省 null 用来表示需要先打开选择窗口的停止命令。

`session`、`settings` 保存上层传入的对象引用，readonly 不让窗口之后换成另一实例。会话生命周期由 Program 管理，本窗口不在 Closed 中 Dispose 它。

`pages = []` 是空数组集合表达式，之后构造函数会填入页面对象。`pickerView = null!` 中的 `!` 只是告诉编译器此字段稍后会正确初始化，不会在运行时创建占位视图。使用它之前仍必须完成真正赋值。

```csharp
private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(RefreshIntervalMilliseconds) };
private readonly ConcurrentQueue<string> output = new();
private bool busy;
private bool maximized;
private string? refreshError;
```

`DispatcherTimer` 的 Tick 由 UI 调度器执行，不是在独立线程按精确间隔打断窗口。500 毫秒是调度间隔，UI 忙时可延后。

`ConcurrentQueue<string>` 是线程安全队列。输出来源先入队，真正写 WPF 文本框由 UI 刷新完成。队列线程安全不代表所有窗口控件可以在后台线程随意修改。

`busy` 表示窗口命令正在执行；`maximized` 记住最近非最小化状态是否最大化；`refreshError` 用于避免相同状态读取错误每半秒重复提示。它们分别有独立生命周期含义。

## 3. 构造函数先建立数据，再开放交互

```csharp
this.session = session;
settings = userSettings;
InitializeComponent();
RestoreWindow();
MachineLabel.Text = Environment.MachineName;
EnvironmentLabel.Text = session.ProjectSettings.EnvironmentName;
EnvironmentLabel.ToolTip = EnvironmentLabel.Text;
pages = CreatePages();
pickerView = new ListCollectionView(pages);
PickerResults.ItemsSource = pickerView;
```

先保存依赖，创建控件，再恢复窗口尺寸。环境名来自控制台项目配置，不是即时探测服务器环境；机器名来自本机环境 API。

`CreatePages` 为每个 ServerInstance 创建 `ProcessPage`。pages 是所有页签的数据数组，`ListCollectionView` 则为选择器提供可过滤视图。设置 Filter 会改变选择器看到哪些项，不会从 pages 中删除服务器实例。

`ItemsSource` 把数据交给 WPF 控件，具体如何显示字段由 XAML 模板和绑定决定。本文件不为每个进程手工创建一套文本控件。

## 4. 菜单映射中的方法组、lambda 与 switch 表达式

```csharp
Commands.ItemsSource = session.ProjectSettings.Commands.Select(action => action switch
{
    ConsoleAction.Start => new ServerCommand("启动服务器", "\uE768", session.StartAsync),
    ConsoleAction.Stop => new ServerCommand("关闭服务器", "\uE71A"),
    ConsoleAction.ExportProtobuf => new ServerCommand("导出 Protobuf", "\uE74E", session.ExportAsync),
    ConsoleAction.Build => new ServerCommand("编译服务器 C#", "\uE90F", () => session.BuildAsync(false)),
    ConsoleAction.BuildAndRestart => new ServerCommand("编译并重启", "\uE72C", () => session.BuildAsync(true)),
    _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知的服务器命令")
}).ToArray();
```

`Select` 逐项转换配置中的枚举动作，`action switch` 为每个动作产生一条 ServerCommand。`ToArray` 让转换在构造时完成；未知枚举值会立即暴露，不显示一个没有定义行为的菜单项。

`session.StartAsync` 没有括号，是方法组转换成委托。它保存“以后可以调用此方法”，不会在窗口创建时马上启动服务器。

`BuildAsync` 需要 bool 参数，不能直接当作无参数 `Func<Task>`，于是用 `() => session.BuildAsync(false)` 固定参数。用户真正执行菜单时，lambda 才调用它。true 的那条表示编译后重启。

停止项只传名称和图标，没有 Execute，因此使用默认 null。后面 `RunCommand` 看到 null 会打开停止选择对话框，这是一种由当前菜单结构约定的特殊分支。

图标字符串里的 `\uE768` 等是 Unicode 转义字符，XAML 的图标字体负责把它显示成图形；它不参与命令标识或业务类型判断。

## 5. 初始选中页签、警告和事件订阅

```csharp
InitializeLogs();
ProcessTabs.ItemsSource = pages;
ProcessTabs.SelectedItem = pages.FirstOrDefault(page => page.Role == settings.SelectedRole) ?? pages.FirstOrDefault();
```

先建立日志视图，再绑定页签。`FirstOrDefault(predicate)` 找到保存的显示名对应页面，找不到得到 null；`??` 再选择第一项，数组为空时仍可为 null。

这会触发 XAML 绑定的选择变化处理，另一部分 `TabChanged` 设置 active 并加载该页日志。`ProcessPage.Role` 虽然叫 Role，实际取 `ServerInstance.Name`，如 Router7；它不是固定的进程类型 Router。

```csharp
if (settings.Warning is { Length: > 0 } warning)
{
    output.Enqueue(warning);
    OperationStatus.Text = warning;
    SetOutputOpen(true);
}
```

属性模式要求 Warning 非 null 且长度大于零，同时把它绑定为 warning。偏好加载警告先放到命令输出并展示，不会因为有警告就禁止整个窗口使用。

```csharp
session.Output += output.Enqueue;
session.InstancesChanged += ReloadInstances;
timer.Tick += (_, _) => RefreshWindow();
```

`+=` 订阅事件。`output.Enqueue` 和 `ReloadInstances` 都是方法组；Tick 使用 lambda 忽略事件的 sender/args，两个 `_` 表示此处不用参数。

Loaded 回调先 `RefreshWindow()` 再 `timer.Start()`。Closed 回调停止 timer，并用 `-=` 解除两个会话事件订阅。会话对象可能比窗口活得更久，退订可以避免继续把消息送给已关闭窗口。

这些事件订阅没有自动后台线程含义。会话事件在触发者所在调用链中执行，timer/窗口事件由 WPF UI 线程调度。

## 6. CreatePages 与 ReloadInstances 如何保存显示偏好

```csharp
var result = session.Instances.Select(instance => new ProcessPage(instance)).ToArray();
foreach (var page in result)
{
    if (!settings.Logs.TryGetValue(page.Role, out var preference))
    {
        continue;
    }

    page.Keyword = preference.Keyword;
    page.MatchesOnly = preference.MatchesOnly;
    page.Following = preference.Following;
}
```

每页引用一个槽位，保存关键词、只显示匹配行、自动跟踪最新等 UI 偏好。`TryGetValue` 同时判断字典里是否存在该键，并通过 out 带出值。没有记录就 continue，保留 ProcessPage 默认设置。

偏好键是 page.Role，也就是当前显示名。动态身份变化时名字可能变，因此旧名字的偏好不一定能匹配新页面；此代码没有另外按稳定 SlotName 迁移偏好。

`ReloadInstances()` 在会话替换槽位列表时被触发：先 SaveSettings 保留旧页面偏好，再重新 CreatePages、建立过滤视图、重绑两个 ItemsSource，最后恢复选中项。

普通 PID、状态、实际名字变化不需要重建整个列表。[ProcessPage](ProcessPage.cs.md) 的 `Refresh` 更新属性并触发 `INotifyPropertyChanged`，WPF 由此刷新绑定。它返回 true 的主要含义是 PID 变了，需要重置该页日志位置。

## 7. RestoreWindow：限制尺寸并记住最大化状态

```csharp
var work = SystemParameters.WorkArea;
MinWidth = Math.Min(MinWidth, work.Width);
MinHeight = Math.Min(MinHeight, work.Height);
Width = Math.Clamp(double.IsFinite(settings.Width) && settings.Width > 0 ? settings.Width : Width, MinWidth, work.Width);
Height = Math.Clamp(double.IsFinite(settings.Height) && settings.Height > 0 ? settings.Height : Height, MinHeight, work.Height);
```

先取得系统工作区，限制最小尺寸不超过可用空间。保存尺寸若为 NaN、无穷或非正数，就用 XAML/窗口当前尺寸；`Math.Clamp` 再把它夹在最小尺寸与工作区大小之间。

这里不是简单相信用户配置里的数字。偏好文件可能残留旧屏幕尺寸或无效值，窗口需要在当前工作区范围内恢复。

随后设置手动位置，把窗口居中，而不是恢复曾经保存的 Left/Top 坐标；当前设置只保存宽高和最大化标志。

```csharp
maximized = settings.Maximized;
if (maximized)
{
    WindowState = WindowState.Maximized;
}

StateChanged += (_, _) =>
{
    if (WindowState != WindowState.Minimized)
    {
        maximized = WindowState == WindowState.Maximized;
    }
};
```

窗口最小化时不改 maximized，所以“最大化 → 最小化 → 关闭”仍能记住之前是最大化。这个 bool 不是完全冗余的当前状态缓存，它保留的是最近非最小化历史。

## 8. SaveSettings 保存的是普通窗口尺寸与页面偏好

```csharp
var bounds = RestoreBounds;
settings.Width = bounds.IsEmpty ? Width : bounds.Width;
settings.Height = bounds.IsEmpty ? Height : bounds.Height;
settings.Maximized = maximized;
settings.SelectedRole = active?.Role;
settings.Logs = pages.ToDictionary(page => page.Role, page => new LogPreference(page.Keyword, page.MatchesOnly, page.Following));
```

`RestoreBounds` 表示窗口还原到普通状态时的边界，用它避免把最大化后的屏幕尺寸写成下一次普通窗口尺寸。没有有效还原矩形时才退回 Width/Height。

`active?.Role` 在没有当前页时得到 null。`ToDictionary` 用每页显示名作键，新建 LogPreference 作值；它要求键唯一，源码没有在这里处理重复显示名的兜底映射。

接下来的 try 只包裹 `settings.Save()`。I/O 或权限错误写到 `Console.Error`，不会因此主动阻止窗口关闭；前面 ToDictionary 等构建数据时的异常不在这个捕获范围内。

这项保存写用户偏好文件，不是写服务器 YAML。不要因为本窗口有 SaveSettings 就推导服务器配置支持写回。偏好文件的加载、警告与落盘格式见 [UserSettings 详解](UserSettings.cs.md)。

## 9. RefreshWindow 每轮先收状态，再更新显示

```csharp
try
{
    session.Refresh();
    refreshError = null;
}
catch (Exception error)
{
    if (refreshError != error.Message)
    {
        refreshError = error.Message;
        ReportError("读取进程状态失败", error);
    }
}
```

会话依次刷新槽位。成功就清空 refreshError；失败时按错误消息去重，只在与上次不同才显示新提示。恢复成功后，同样错误再发生会重新提示，因为记忆已被清空。

这里比较的是 Message 文本，不是异常类型、堆栈或对象身份。catch 范围只是 `session.Refresh`，不是整个窗口刷新过程。

```csharp
foreach (var page in pages)
{
    if (page.Refresh() && page == active)
    {
        ResetLogRows();
    }
}
```

页面从槽位同步 PID、名字和状态；当前页若换了进程，重置日志行视图。即使上面的会话刷新失败，窗口仍尝试显示已经得到的状态，而不是把整个界面刷新提前退出。

随后 `ProcessSummary` 统计状态恰好等于 Ready 的页面数量，再 `UpdateLogs()` 和 `FlushOutput()`。这里的“就绪数量”来自控制台状态推导，不是直接询问引擎当前所有依赖连接是否存在。

## 10. 双击和 Enter 如何找到具体命令

```csharp
private async void CommandDoubleClick(object sender, MouseButtonEventArgs e)
{
    if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject origin &&
        ItemsControl.ContainerFromElement(Commands, origin) is ListBoxItem { DataContext: ServerCommand command })
    {
        await RunCommand(command);
    }
}
```

WPF 路由事件可能来自条目里面的图标或文字，`OriginalSource` 未必是 ListBoxItem。先确认它是 DependencyObject，再用 `ContainerFromElement` 找所属条目，最后从 DataContext 匹配出 ServerCommand。

模式里的 `command` 只在匹配成功时可用；点击空白或其他对象不会执行命令。左键检查避免把任意鼠标按钮都当成双击执行。

键盘路径检查 `e.Key == Key.Enter` 且 SelectedItem 是 ServerCommand，设置 `e.Handled = true`，然后等待同一 `RunCommand`。

事件处理器是 `async void`，因为 WPF 事件委托需要 void 返回；普通业务方法仍用 Task，让调用方能等待并观察失败。`async void` 本身不能被调用方 await，所以主要命令错误在 RunCommand 内处理很重要，但不能说它自动捕获所有窗口事件异常。

## 11. RunCommand 先处理重入与停止选择

```csharp
if (busy)
{
    return;
}

var execute = command.Execute;
if (execute == null)
{
    var dialog = new StopServersWindow(pages) { Owner = this };
    if (dialog.ShowDialog() != true)
    {
        return;
    }

    var selected = dialog.SelectedServers;
    execute = () => session.StopAsync(selected);
}
```

窗口 busy 时直接忽略额外执行请求。会话还有自己的 busy 防止底层命令重入；前者服务于 UI，后者保护会话操作，两者不是同一个字段。

没有 Execute 时创建 [StopServersWindow](StopServersWindow.xaml.cs.md)，Owner 指向当前窗口。`ShowDialog()` 返回可空 bool，只有明确 true 才继续；false 或 null 都返回。取消选择不会设置忙碌状态或发出停止命令。

`execute = () => session.StopAsync(selected)` 捕获这次选择，形成无参数异步函数。此时仍未停止，后面执行 `execute()` 才发生。

这段对话框创建与显示在后面的 try 之前，所以 RunCommand 的那个 catch 不覆盖所有对话框准备异常。

## 12. 执行期间禁用命令，finally 恢复

```csharp
busy = true;
Commands.IsEnabled = false;
SetOutputOpen(true);
OperationStatus.Text = $"{command.Name}…";
OperationStatus.Foreground = (Brush)FindResource("Muted");
output.Enqueue($"[{DateTime.Now:HH:mm:ss}] {command.Name}");
try
{
    await execute();
    OperationStatus.Text = $"已完成 · {command.Name}";
}
catch (Exception error)
{
    ReportError(command.Name + "失败", error);
}
finally
{
    busy = false;
    Commands.IsEnabled = true;
    RefreshWindow();
}
```

先禁用菜单、打开输出区、显示进行中状态并入队一条带本地时间的消息。`FindResource("Muted")` 从 WPF 资源中取颜色画刷，显式 `(Brush)` 转换为属性所需类型。

`await execute()` 在任务未结束时让 UI 消息循环继续工作，计时器仍可刷新服务器状态。正常 WPF 调用上下文下，没有 ConfigureAwait(false) 的续体会回到 UI 调度，所以后续可更新控件。

成功后显示“已完成”意味着命令任务完成，具体含义取决于命令。例如启动命令返回并不表示每个服务器已经 PROCESS_READY；就绪仍靠后续刷新观察。

错误通过 ReportError 显示。finally 在任务正常或异常结束时恢复菜单与 busy，再刷新一次。源码在 try 前还有资源查找等操作，因此不能绝对说从设置 busy 开始的任意异常都必然被该 finally 覆盖。

## 13. OnClosing：命令期间阻止关闭，平时保存偏好

```csharp
protected override void OnClosing(CancelEventArgs e)
{
    if (busy)
    {
        e.Cancel = true;
        OperationStatus.Text = "命令执行中，完成后可关闭控制台。";
    }

    base.OnClosing(e);
    if (!e.Cancel && IsLoaded)
    {
        SaveSettings();
    }
}
```

`override` 重写基类虚方法，`protected` 表示面向本类和派生访问。`CancelEventArgs` 的 Cancel 可请求取消这次关闭。

命令执行中将其设真并提示。随后仍调用基类，让正常关闭事件流程继续；只有最终没有取消且窗口已加载才保存偏好。

这个方法没有调用 `session.StopAsync`。关闭窗口只是结束控制台 UI；真正关闭服务器需要执行对应菜单命令。Closed 回调退订事件，Program 最后释放会话持有的进程包装对象，服务器本身可继续运行。

## 14. ReportError 和输出区开合

```csharp
private void ReportError(string title, Exception error)
{
    OperationStatus.Text = title + " · " + error.Message;
    OperationStatus.Foreground = Brushes.Firebrick;
    output.Enqueue($"[{DateTime.Now:HH:mm:ss}] {title} · {error.Message}");
    SetOutputOpen(true);
}
```

错误同时进入短状态栏和命令输出队列。这里显示的是 Message，没有自动展开堆栈，也没有重新抛出异常。

`ToggleOutput` 使用 `!OutputBox.IsVisible` 反转当前可见状态。`SetOutputOpen` 把 Visibility 设为 Visible 或 Collapsed，并更新展开箭头。Collapsed 不只隐藏绘制，还让控件在布局中不占这部分空间。

折叠不会清空队列或暂停 FlushOutput。命令输出与服务器日志是两套数据：OutputBox 显示构建和操作消息，LogList 显示 ServerInstance 收到的服务器原始行及其解析视图。

## 15. FlushOutput：整行批量、限制历史、保持阅读位置

```csharp
if (output.IsEmpty)
{
    return;
}

var batch = new StringBuilder();
while (batch.Length < MaxOutputBatchCharacters && output.TryDequeue(out var line))
{
    batch.AppendLine(line);
}
```

`StringBuilder` 避免每行都创建一个新的完整拼接字符串。`TryDequeue` 原子地尝试从并发队列取一条，失败即停止。前面的 IsEmpty 只是快速路径，真正取数据仍依赖 TryDequeue。

批量阈值 65536 是追加下一行前检查的，因此最后一整行可以使批次超过阈值。它不是严格单行长度限制，也没有给整个待处理队列设置最大容量。

```csharp
var atEnd = OutputBox.VerticalOffset >= OutputBox.ExtentHeight - OutputBox.ViewportHeight - ScrollEndTolerance;
if (OutputBox.Text.Length > MaxOutputCharacters)
{
    var text = OutputBox.Text;
    var split = text.IndexOf('\n', text.Length - RetainedOutputCharacters);
    OutputBox.Text = split >= 0 ? text[(split + 1)..] : text[^RetainedOutputCharacters..];
}
```

更新前先记住是否已靠近底部：内容总高度减视口高度是最大滚动偏移，再允许 2 个布局单位误差。

已有文本超过 160000 字符时，从末尾约 100000 字符的位置寻找换行，优先从下一行开始保留，避免保留首行只剩半句。找不到换行就直接保留最后 100000 字符。

`text[(split + 1)..]` 取某位置到末尾，`text[^RetainedOutputCharacters..]` 用 `^` 从末尾倒数位置切片。两者都是字符串范围语法。

裁剪发生在追加新批次之前，因此追加后文本可暂时超过阈值，下次有输出时再裁剪；队列空时本方法提前返回，不会仅为文本超长主动再裁一次。

最后 AppendText 一次追加整个 batch，仅在更新前 atEnd 为真时显式 ScrollToEnd。用户已经滚到旧输出时不会被每次新增消息强制拉回末尾。

## 16. 打开实例选择器并筛选结果

`OpenPicker` 只设置 `Picker.IsOpen = true`。打开事件再清空搜索、选中 active、滚动到它、给搜索框焦点。这些是 UI 选择，不会切换服务器进程或重启服务。

```csharp
var term = PickerSearch.Text.Trim();
pickerView.Filter = item => item is ProcessPage page && (page.Role.Contains(term, StringComparison.OrdinalIgnoreCase) ||
    (page.Pid?.ToString().Contains(term, StringComparison.Ordinal) ?? false));
PickerResults.SelectedIndex = pickerView.Count > 0 ? 0 : -1;
```

Filter 是接收一个条目并返回 bool 的委托。条目先要能匹配成 ProcessPage，再要求显示名忽略大小写包含 term，或 PID 文本包含 term。

`page.Pid?.ToString()` 在 PID 为 null 时得到 null，后面整条条件链通过 `?? false` 变成不匹配。空 term 会被每个普通名字包含，所以清空搜索能恢复全部页面。

有匹配项选第一条，没有则 SelectedIndex = -1。筛选只改变 pickerView 的可见项，主页面数组不受影响。

## 17. 选择器里的方向键、Enter、Escape 与鼠标

```csharp
if (e.Key == Key.Down && PickerResults.SelectedItem is { } selected)
{
    PickerResults.ScrollIntoView(selected);
    (PickerResults.ItemContainerGenerator.ContainerFromItem(selected) as ListBoxItem)?.Focus();
    e.Handled = true;
    return;
}

PickerResultsKeyDown(sender, e);
```

搜索框按下方向键且有选中结果时，先滚动到结果，再尝试找到其 ListBoxItem 容器并 Focus。`as ListBoxItem` 转换失败得到 null；`?.Focus()` 在还没生成容器时不调用，因此“尝试移动焦点”不等于保证每次立刻成功。

其他键交给 `PickerResultsKeyDown`。该方法对 Enter/Escape 以外的键立即返回；Enter 且有 ProcessPage 时 ChoosePage，Escape 不改页签。两者都关闭选择器、标记事件已处理并把焦点还给打开按钮。

鼠标 `ChooseProcess` 同样从 OriginalSource 找所属 ListBoxItem，从 DataContext 取得 ProcessPage，再 ChoosePage 并关闭选择器。它没有另做一次数据库或服务器查询。

`ChoosePage` 设置 `ProcessTabs.SelectedItem` 并 ScrollIntoView。日志页的实际切换继续由已连接的 TabChanged 处理，避免这里重复实现日志清理与加载流程。

## 18. 窗口快捷键与剪贴板

选择器打开时，`WindowKeyDown` 先返回，让选择器自己的按键逻辑处理。其他时候按顺序检查：

| 条件 | 动作 |
| --- | --- |
| 精确 Ctrl+F | 聚焦日志 FilterBox 并全选关键词 |
| F3 | FindMatch，带 Shift 则向前，否则向后 |
| FilterBox 有键盘焦点时 Enter | 同样在匹配项之间移动 |
| 精确 Ctrl+C 且焦点位于 LogList 内 | 复制选中原始日志 |

`Keyboard.Modifiers == ModifierKeys.Control` 是精确比较，所以额外按住其他修饰键不满足这条 Ctrl 判断。F3 路径使用 HasFlag(Shift)，只关心是否含 Shift，因此行为不同。

`FindMatch(... ? -1 : 1)` 用 -1/1 表示方向；具体定位逻辑在 [日志视图代码](MainWindow.Logs.cs.md)。只有命中一项快捷键才设置 `e.Handled = true`，其他按键保持原来的 WPF 路由机会。

```csharp
if (LogList.SelectedItem is not LogEntry row)
{
    return;
}

try
{
    Clipboard.SetText(row.Raw);
}
catch (COMException error)
{
    ReportError("复制日志失败", error);
}
```

没有选中 LogEntry 就直接返回。复制的是 `row.Raw` 原始记录，不是筛选后显示的简化文字。剪贴板是系统共享资源，COM 调用失败时显示错误；源码只捕获 COMException，不是所有可能异常。

右键“复制原始日志”和快捷键可以共用这个处理器，因为它们都只依赖当前选中行，不依赖特定鼠标位置。

## 19. 把一个完整交互串起来

以“编译并重启”为概念操作例：菜单双击取得 ServerCommand → RunCommand 禁用菜单并 await 委托 → ConsoleSession 编译、准备副本、停止旧组、启动新组 → 输出事件先入队 → UI timer 继续刷新并批量显示 → 命令任务完成后恢复菜单 → 后续日志刷新逐步显示实际身份和就绪状态。

这个流程里只有 ConsoleSession 明确的文件复制使用 Task.Run。MainWindow 的控件更新依赖 UI 调度；等待子进程不等于阻塞 UI 线程，也不等于服务器已经就绪。

本文件不负责数据库初始化、引擎连接状态机或日志落盘。它把这些对象提供的证据组合为界面。本文完成的是源码与交互分支对照，没有启动 WPF、编译项目或运行界面测试。
