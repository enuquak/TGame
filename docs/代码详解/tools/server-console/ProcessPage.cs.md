# ProcessPage.cs 代码详解

源码：[ProcessPage.cs](../../../../tools/server-console/ProcessPage.cs)

## UI 适配层

ProcessPage 把 ServerInstance 适配成 WPF 可绑定的页签。Instance 是底层权威进程对象，Role、Pid、Status 保存上次通知界面时的快照，用于比较变化并触发 PropertyChanged。这里的 Role 实际显示 Instance.Name，即带编号的实例名，不是 ServerInstance.Role 的进程类型。

PidLabel、Description、Dot 和 CanStop 都是派生属性。Dot 将就绪映射为绿色，启动/初始化为蓝色，连接不全/停止中为黄色，失败为红色，其余为灰色。CanStop 直接读取 Instance.Alive，不额外存一份可停止标志。

## 页面独有状态

ClearedThrough 是用户点击清空后已经隐藏到哪一条绝对日志行；TopLine 与 SelectedLine 记录切换页签前的滚动位置和选中行。这些信息没有写回日志文件，也不影响服务器。

Keyword、MatchesOnly、Following 保存每个页签各自的搜索和跟踪偏好。MainWindow 从 UserSettings 初始化这些属性，并在保存设置时写回。新建页签默认跟踪最新日志。

## 刷新和通知

Refresh 首先比较底层 PID 与已显示 PID。发生进程替换时清除 ClearedThrough、TopLine、SelectedLine，避免新进程沿用旧生命期行号。随后只要 PID、状态或实例名变化，就更新三个快照并发送 PropertyChangedEventArgs(null)，要求绑定系统刷新该页的所有属性，包括派生标签和颜色。

返回值只表示 PID 是否改变。MainWindow.RefreshWindow 使用它决定当前页是否需要重建日志行；单纯连接状态变化只刷新标签，不清空已经读取的日志。动态认领实例号也能更新 Role，而稳定启动槽位仍由 ServerInstance 内部管理。
