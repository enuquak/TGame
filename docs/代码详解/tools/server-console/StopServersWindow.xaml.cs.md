# StopServersWindow.xaml.cs 代码详解

源码：[StopServersWindow.xaml.cs](../../../../tools/server-console/StopServersWindow.xaml.cs)

## 职责

这个对话框只收集要关闭的进程，不调用 StopAsync，也不持有服务器会话。MainWindow 创建它并设置 Owner；只有 ShowDialog 返回 true 后，主窗口才把 SelectedServers 交给 ConsoleSession.StopAsync。

构造函数完成 XAML 初始化，把传入 ProcessPage 集合设为 Choices.ItemsSource，再更新选择计数。它复用主窗口页面对象，因此名称、PID 和存活信息与主界面来自相同 ServerInstance。

## 选择的权威来源

SelectedServers 是计算属性，每次从 Choices.SelectedItems 中取 ProcessPage，再过滤 CanStop，最后映射为 ServerInstance 数组。没有额外保存选中列表，也不会仅凭视觉上已经勾选就允许关闭死亡进程。

SelectAll 只遍历当前 CanStop 的页面，且先检查是否已选，避免重复加入 SelectedItems。DeselectAll 直接清空 ListBox 选择。SelectionChanged 统一触发 UpdateCount，因此鼠标选择和全选操作共享计数逻辑。

## 确认与失败边界

UpdateCount 使用 SelectedServers.Count 显示“已选 N 个”，并仅在大于 0 时启用 Confirm。ConfirmStop 在点击时再次计算，防止此前选中的进程已经退出导致空操作；仍有效时设置 DialogResult=true，使模态窗口结束。

取消按钮由 XAML 的 IsCancel 处理，没有关闭服务器的副作用。即使确认后某个进程立即退出，实际生命周期竞态仍由 ServerInstance.StopAsync 的存活检查处理，对话框不实现进程锁或额外生命周期状态。
