# StopServersWindow.xaml 代码详解

源码：[StopServersWindow.xaml](../../../../tools/server-console/StopServersWindow.xaml)

## 模态关闭窗口

该 XAML 定义内部 StopServersWindow，默认 430×500，最小 370×400。ShowInTaskbar=False 避免出现第二个任务栏入口，CenterOwner 让它相对主窗口居中，使用 ConsoleTheme.xaml 保持字体、颜色和按钮样式一致。

DockPanel 的顶部是标题、CountLabel、全选与取消选择；底部是取消和红色“关闭所选”按钮；中间 Choices 占满剩余区域。底部确认按钮初始禁用，启用条件由后台的有效选择数量决定。

## 多选列表

Choices 的 SelectionMode=Multiple，SelectionChanged 连接后台更新计数。每个 ListBoxItem 的 IsEnabled 绑定 CanStop，因此已停止进程仍可显示状态，但不能参与新的选择。

项模板右侧显示 Description，包含状态与 PID；左侧用 CheckBox 显示 Role。这个 CheckBox 设置 IsHitTestVisible=False 和 Focusable=False，只作为 ListBoxItem.IsSelected 的单向视觉投影。实际选择由列表项统一管理，避免复选框状态与列表选中集合成为两个独立来源。

## 事件与结果

全选、取消选择和确认分别调用 SelectAll、DeselectAll、ConfirmStop。取消按钮设置 IsCancel=True，支持 WPF 标准取消行为。确认不会直接终止进程；后台检查选择后设置 DialogResult，主窗口获取 SelectedServers 才执行停止命令。

红色背景仅标识有关闭副作用的动作。真正的停止目标始终来自后台对 CanStop 的再次过滤，不能把按钮颜色或列表勾选当成进程仍存活的保证。
