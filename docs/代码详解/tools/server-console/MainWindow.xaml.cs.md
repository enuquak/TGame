# MainWindow.xaml.cs 代码详解

[源码](../../../../tools/server-console/MainWindow.xaml.cs)

## XAML、窗口代码与会话

WPF 用 XAML 描述控件、样式、绑定与事件名。这里的 `MainWindow : Window` 是窗口的 C# 实现。 `partial` 允许几个文件共同实现同一个类型，因此这里可以直接调用另一个文件中的 `InitializeLogs`、`UpdateLogs` 和 `FindMatch`，也能访问其中的 `active`。 `InitializeComponent()` 由 WPF 生成，用来创建 XAML 控件并连好事件。 [窗口布局](MainWindow.xaml.md) · [日志视图代码](MainWindow.Logs.cs.md)

| 成员 | 谁读写它 |
| --- | --- |
| `session` | 窗口用它执行服务器命令和刷新进程。窗口由 Program 创建，Program 负责释放会话 |
| `settings` | 记录还原尺寸、是否最大化、活跃页和每页搜索偏好 |
| `pages`、`pickerView` | `ProcessPage[]` 作为页签数据，`ListCollectionView` 为弹层提供同一批对象的过滤视图 |
| `timer` | WPF `DispatcherTimer`，窗口加载后每 500 毫秒尝试刷新，回调由 UI 线程调度 |
| `output` | 线程安全的 `ConcurrentQueue<string>`，先收集命令输出，等 UI 批量向文本框追加 |
| `busy`、`maximized`、`refreshError` | 分别记录窗口命令执行中、上次非最小化状态是否最大化，以及上一次刷新错误的文本 |

窗口的 `busy` 管菜单和窗口关闭，会话自己的 `busy` 防止进程命令重入。 `DispatcherTimer` 依赖 UI 消息循环，500 毫秒是调度间隔，界面忙碌时可能延后。

## 构造、数据绑定和重建页签

1. 构造函数保存传入的会话与偏好，调用 `InitializeComponent`、`RestoreWindow`，将机器名、项目环境名分别写入 `MachineLabel` 和 `EnvironmentLabel`。
2. `CreatePages` 为每个 `ServerInstance` 创建 `ProcessPage`，以 `page.Role` 在 `settings.Logs` 中查找关键字、仅匹配行、跟踪最新三项显示偏好。这里 `ProcessPage.Role` 取实例的显示名，例如 `Router7`。 `pickerView` 日后修改的是视图中的可见项，页签数组保持完整。
3. `Commands.ItemsSource` 接收从项目动作列表映射出的 `ServerCommand` 记录。每项包含显示名称、图标字符和可空 `Func<Task>`。 `session.StartAsync` 是方法组，可直接作为无参数委托。 `() => session.BuildAsync(true)` 则用 lambda 把参数固定在回调里。停止项不设置委托，因为执行前要选进程。
4. `InitializeLogs` 先让 `LogList` 具备数据视图。随后将 `pages` 绑定到 `ProcessTabs`，通过 `FirstOrDefault` 尝试按保存的显示名恢复页签，找不到就选第一项。XAML 中的 `SelectionChanged="TabChanged"` 负责把选择交给日志视图代码。若偏好加载产生 `Warning`，构造函数打开命令输出并显示该提示。
5. `session.Output += output.Enqueue` 订阅输出，`InstancesChanged += ReloadInstances` 监听启动计划切换。窗口 `Loaded` 时刷新一次并启动计时器。 `Closed` 停止计时器并用 `-=` 解订阅，避免会话继续把输出和实例变化送进已关闭的窗口。

`ReloadInstances` 在计划槽位名称序列变化时调用。它先 `SaveSettings` 保留旧页偏好，再调用 `CreatePages` 并换掉页签、选择器的绑定，重新尝试选中旧显示名。正常的 PID、状态或显示名变动由 `ProcessPage.Refresh` 发属性变更通知，WPF 根据 `INotifyPropertyChanged` 更新绑定。偏好按当时的显示名存取，运行期发生动态认领后，旧显示名的偏好可能无法在新页直接匹配。 [ProcessPage 详解](ProcessPage.cs.md) · [ConsoleSession 详解](ConsoleSession.cs.md)

## 窗口状态和每次刷新

`RestoreWindow` 取 `SystemParameters.WorkArea`。最小尺寸先降到工作区容纳范围，保存尺寸只有有限且为正才采用，再用 `Math.Clamp` 约束到最小和可用尺寸之间。随后用工作区原点和宽高计算居中坐标，恢复最大化。 `StateChanged` 在非最小化状态才更新 `maximized`，因此最大化后再最小化仍保留最大化偏好。

`SaveSettings` 优先读取 `RestoreBounds` 的还原尺寸，保存 `active?.Role`，用 `ToDictionary` 收集每个页签的日志偏好。个人设置的实际路径由 Program 按仓库区分，位于用户本地应用数据下。文件在 `settings.Save()` 内更新，I/O 和权限失败写标准错误。 [个人偏好详解](UserSettings.cs.md)

`RefreshWindow` 先用 `session.Refresh()` 更新进程和日志。相邻刷新报相同错误时，只由 `ReportError` 显示一次，成功刷新会清除去重记录。随后继续刷新窗口的各个 `ProcessPage`，当前页 PID 变化时用 `ResetLogRows` 清除原生命期显示游标。最后计数、调用 `UpdateLogs` 并刷新命令输出。这里统计的是界面状态为 `ServerStatus.Ready` 的项目，运行期普通对端断线不会撤销服务器的首次就绪状态。

## 如何从点击走到服务器命令

`CommandDoubleClick` 验证左键并根据点击来源找到对应 `ListBoxItem`。属性模式 `{ DataContext: ServerCommand command }` 从列表容器取出命令对象。 `CommandKeyDown` 则只在按 Enter 且已有选中命令时执行。设 `e.Handled = true` 表示快捷键已处理。两者是 `async void` 的 WPF 事件处理函数，共同等待返回 `Task` 的 `RunCommand`。

`RunCommand` 的顺序如下。

1. `busy` 时直接返回。命令委托为空时，先打开 `StopServersWindow`，用当前窗口作 `Owner`。只有 `ShowDialog() == true` 才从对话框取选中的活进程，组成调用 `session.StopAsync(selected)` 的委托。取消对话框时方法直接返回。
2. 设置 `busy = true` 并禁用命令列表，展开输出框，把操作名、时间和状态显示出来。
3. `await execute()` 等待会话任务。比如“编译并重启”会等编译和新组启动调用返回，再把状态栏更新为已完成。服务器自身是否就绪由刷新过程确认。
4. 操作异常时调用 `ReportError`，状态栏用红色显示错误，并把带时间的错误放进输出队列。 `finally` 恢复 `busy` 和命令列表，再立即刷新一次。

`OnClosing` 在命令执行期间设置 `e.Cancel = true`，提示等命令结束再关闭。交给基类分发关闭事件后，窗口已加载且关闭未取消时保存偏好。退出窗口后由 Program 释放会话，服务器停止需要经“关闭服务器”命令执行。 [停止选择窗口详解](StopServersWindow.xaml.cs.md)

## 命令输出为什么走队列

`ToggleOutput` 根据 `OutputBox.IsVisible` 把输出框在 `Visible` 和 `Collapsed` 间切换，`SetOutputOpen` 同时更新按钮文案与箭头。折叠只改变可见性。 `FlushOutput` 仍在有队列数据时运行。

`FlushOutput` 从队列取整行追加到 `StringBuilder`。累积长度到 65536 字符就停，最后一条整行可能跨过这个阈值。追加前先算滚动视图离底部是否在 2 个 WPF 布局单位内。已有文本超过 160000 字符时，从最后约 100000 字符开始找换行或直接取文本尾部，再追加新批。只有更新前已经靠近底部的情况下显式 `ScrollToEnd`，用户查看旧编译输出时就不会每次被强制滚回末尾。这个文本框与用于服务器日志的 `LogList` 使用不同数据源。

## 搜索、选择和快捷键

- `OpenPicker` 打开弹层。 `PickerOpened` 清空上次搜索，选中当前页、滚到它并把焦点交给搜索框。
- `PickerSearchChanged` 去掉关键字首尾空白，用忽略大小写的包含匹配检查 `page.Role`，或按字符匹配 PID 文本。 `?.` 在没有 PID 时跳过其后调用，`?? false` 提供结果。匹配项存在时默认选择第一条。例如输入 `router` 可找到 `Router7`，输入 `41` 可找到 PID 含 `41` 的进程。
- 搜索框按 ↓ 时，`PickerSearchKeyDown` 尝试把焦点移给当前结果。其他键交给 `PickerResultsKeyDown`。Enter 有结果时调用 `ChoosePage`，Escape 直接关闭，两者都让焦点回到打开按钮。 `ChooseProcess` 处理鼠标选择，`ChoosePage` 同步主页签并滚动到它的位置。
- `WindowKeyDown` 在弹层打开时提前返回，给弹层自己的按键处理机会。其他时候，Ctrl+F 聚焦日志关键字并全选，F3 或日志搜索框内 Enter 选择后一个匹配，按住 Shift 传入 `-1` 选择前一个。
- 日志列表获得焦点时 Ctrl+C 进入 `CopyLog`，右键“复制原始日志”也调用它。 `Clipboard.SetText(row.Raw)` 复制的是原始日志行。没有选中行则提前返回，剪贴板操作发生 `COMException` 时通过 `ReportError` 展示。
