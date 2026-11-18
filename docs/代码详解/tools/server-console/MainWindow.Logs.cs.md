# MainWindow.Logs.cs 代码详解

源码：[MainWindow.Logs.cs](../../../../tools/server-console/MainWindow.Logs.cs)

## 展示集合与行号

本 partial 文件专管当前页日志。rows 是 ObservableCollection<LogEntry>，logs 是可过滤的 ListCollectionView，active 指向当前 ProcessPage。readThrough 表示已经消费到的绝对行号，matchCount 缓存当前保留行中的匹配数。

ServerInstance.LineCount 累计当前生命期全部已接受记录，但 Lines 只保留最近 1000 行。因此展示代码不能把集合索引当作永久行号：最早保留行号为 LineCount - Lines.Count + 1，LogEntry.Number 使用这一编号体系。

## 切页过程

InitializeLogs 建立视图过滤器；只有 MatchesOnly 为真且关键词非空时才过滤不匹配行。TabChanged 先保存旧页可视顶行与选中行，再切换 active。switching 在给搜索框和复选框赋值期间阻止 FilterChanged 把程序性同步当成用户编辑。

切换后 ResetLogRows 从该页 ClearedThrough 开始，UpdateLogs 读取当前仍保留的记录；此前选中行仍存在且通过过滤时恢复选中。未开启跟踪时恢复原顶行，开启跟踪时由正常更新滚动到底部。

## 增量更新与淘汰

UpdateLogs 无当前页或没有新记录时直接返回。如果源 LineCount 小于 readThrough，说明日志生命期已重置，清除旧页清空边界和滚动/选择行号。

随后计算最早保留行。若展示集合整个早于这一范围，直接清空；否则逐条移除已经被 ServerInstance 淘汰的头部记录，并同步减少 matchCount。新记录只从 max(first, readThrough + 1) 开始解析，避免每个 tick 重建所有行。更新 readThrough 后刷新计数；跟踪状态下滚到最新，否则在头部淘汰后尽量恢复用户原顶行。

## 搜索、导航与清空

FilterChanged 记录当前顶行，更新页面关键词和 MatchesOnly，重新统计匹配并刷新集合视图。原选中对象仍在过滤结果中时保留选择，最后根据跟踪开关滚动或恢复位置。

FindMatch 按 direction 为 1 或 -1 循环遍历当前可见视图，使用模运算在首尾绕回。找到匹配时关闭跟踪、选中并滚动到该行。没有关键词匹配时按钮禁用，导航直接返回。

ClearLog 只把 ClearedThrough 设为当前 LineCount，清除页面滚动与选择，然后重置 rows。磁盘日志和 ServerInstance 的记录没有删除；后续新行仍会显示。页签再次打开时从清空边界继续，避免旧内容立刻回填。

## 视口与 WPF 依赖

LogScroller 首次需要时应用模板，在视觉树递归查找 ScrollViewer 并缓存。ViewportLine 用按项滚动的 VerticalOffset 找到当前顶行编号；RestoreViewport 选择不早于指定编号的最接近可见行。MainWindow.xaml 必须维持 ScrollUnit=Item，此处的偏移解释才成立。

UpdateCounts 同时更新匹配数、保留行数、空列表提示和导航按钮。该文件不读取磁盘、不管理 Process 生命周期，也不推断服务器健康状态，所有输入都来自当前 ProcessPage 和 ServerInstance。
