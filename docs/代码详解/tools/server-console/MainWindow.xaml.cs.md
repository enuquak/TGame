# MainWindow.xaml.cs 代码详解

源码：[MainWindow.xaml.cs](../../../../tools/server-console/MainWindow.xaml.cs)

## 主窗口的职责

这个 partial 类管理命令、窗口偏好、进程选择器和周期刷新；日志行维护放在 MainWindow.Logs.cs，布局由 MainWindow.xaml 声明。session 提供实际进程操作，settings 保存个人显示偏好。pages 是每个 ServerInstance 对应的 ProcessPage，pickerView 为搜索弹层提供过滤视图。

DispatcherTimer 每 500 毫秒刷新一次。命令输出通过 ConcurrentQueue 接收，防止异步编译输出直接改 WPF 控件；FlushOutput 再在 UI 线程批量追加。busy 用于禁用菜单和阻止操作中关闭窗口，session 内部还有独立的命令重入保护。

## 构造和生命周期

构造函数先加载 XAML、恢复窗口尺寸、显示机器名和项目环境名，再从实例创建页签并恢复每页搜索偏好。ConsoleAction 被映射成 ServerCommand 记录，包含中文标题、图标和异步委托。Stop 的委托为 null，表示执行前需打开选择进程对话框。

InitializeLogs 建立日志视图后，ProcessTabs 绑定 pages，并优先选中个人设置中的实例。坏偏好文件的 Warning 会进入命令输出，用户能够看到恢复默认值的原因。

Loaded 首次刷新并启动 timer；Closed 停止 timer、解除 Output 和 InstancesChanged 订阅，避免已关闭窗口继续接收会话事件。OnClosing 在命令执行中取消关闭；正常关闭时保存个人设置，不主动停止服务器。

## 刷新与换组

RefreshWindow 先调用 session.Refresh，并对相同错误消息去重，避免每个 tick 重复输出。再刷新各 ProcessPage；当前页的 PID 改变时清空展示游标。最后更新实例/就绪总数、日志和命令输出。

InstancesChanged 触发 ReloadInstances。它先保存旧页偏好，再按新启动计划重建 pages 与选择器视图。仅数量改变后的新组走这条路径，连接状态变化通过属性通知处理。

RestoreWindow 将有限、正数的个人尺寸约束到最小窗口尺寸和当前工作区，居中定位，恢复最大化状态。StateChanged 不把最小化当成取消最大化。SaveSettings 使用 RestoreBounds 保存还原后的尺寸，并记录当前页及每页筛选偏好；落盘失败写标准错误而不打断关闭。

## 命令执行与输出

双击或 Enter 找到 ServerCommand 后进入 RunCommand。Stop 先打开 StopServersWindow；取消则立即返回，确认后才调用 session.StopAsync。执行期间禁用 Commands、展开输出、设置操作状态并记录时间。异常由 ReportError 显示，finally 恢复菜单并刷新状态。

FlushOutput 每次最多汇集约 65536 字符。已有文本超过 160000 字符时，从合适换行处保留约 100000 字符；追加前记录用户是否接近末尾，只有原先在末尾时自动跟随，避免查看历史时被拖走。

## 选择器和快捷键

进程选择器按显示实例名或 PID 过滤。打开时清空搜索、选中当前页并聚焦输入；方向键进入结果，Enter 选择，Escape 关闭，鼠标点击也可选择。ChoosePage 同步主页签并滚动到可见位置。

Ctrl+F 聚焦日志搜索，F3/Shift+F3 或搜索框 Enter 遍历匹配。日志获得焦点时 Ctrl+C 和右键复制都使用 LogEntry.Raw；剪贴板 COMException 转成可见操作错误。弹层打开时主窗口快捷键不抢走选择器键盘事件。
