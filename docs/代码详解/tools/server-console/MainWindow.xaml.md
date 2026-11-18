# MainWindow.xaml 代码详解

源码：[MainWindow.xaml](../../../../tools/server-console/MainWindow.xaml)

## 窗口结构

这是内部 MainWindow 类的 WPF 声明，默认 1250×760，最小 960×590，使用微软雅黑和 Segoe UI，并启用布局像素对齐。PreviewKeyDown 接到代码后台统一处理日志快捷键。

顶层 Grid 分为固定 205 像素的左侧操作栏与右侧主区域。左侧包含品牌、EnvironmentLabel、Commands 和机器名；环境文字与命令列表由 ConsoleSession 的项目配置提供，XAML 没有写死服务器实例数量。右侧按标题/选择器、实例页签、日志主体、命令输出四个区域排列。

## 资源和交互样式

窗口合并 ConsoleTheme.xaml，并增加本页专用的 SectionLabel、CommandItem、ProcessTab、LogItem 样式。命令项通过背景区分悬停和选择；页签选中后显示底部强调线；日志行用较深背景表现悬停/选中。样式改变外观，不替代后台命令判断。

Commands 绑定 ServerCommand.Icon 与 Name，鼠标双击和 Enter 分别交给后台事件。AutomationProperties.Name 给命令列表、进程搜索、页签、日志和匹配导航提供可访问名称。

## 实例页签与搜索

Picker 是锚定 PickerButton 的 Popup，StaysOpen=False 支持点击外部关闭。PickerSearch 提供名称或 PID 筛选；PickerResults 每项显示状态 Dot、Role 和 Description。打开、文本变化、键盘与鼠标选择分别连到后台处理。

ProcessTabs 使用横向 StackPanel，允许水平滚动、禁用垂直滚动，项模板显示状态点与实例名。它是一个 ListBox，SelectionChanged 触发 TabChanged，不为每个实例建立独立日志 ListBox。ActiveHeading 随当前 ProcessPage 更新名称、状态和 PID 标签。

## 日志工具条与正文

工具条包含 FilterBox、上一/下一匹配、OnlyMatches、Follow 和 ClearLog。搜索框为空时覆盖提示文字；提示 IsHitTestVisible=False，不阻挡输入。清空按钮的说明明确仅清空显示，保留磁盘日志。

LogList 三列宽度为时间 108、事件 178、内容剩余空间。它启用虚拟化与 Recycling，并设置 ScrollUnit=Item，和后台按行恢复视口的实现一致。事件与内容允许换行；列里的 LogText 通过父 ListBox.Tag 取得搜索关键词，在虚拟化复用时重新高亮。

右键菜单 CopyLog 复制原始记录。EmptyLog 放在同一个容器中，用于区分暂无日志和没有匹配；其鼠标命中关闭，不拦截列表。

## 命令输出和边界

底部 OutputToggle 控制只读 OutputBox 的展开；OperationStatus 显示当前操作和错误。命令输出使用独立 TextBox，不混入实例结构化日志。后台负责限量保留文本与滚动跟随。

XAML 自身不创建进程、不读取 YAML，也不保存设置；它依赖代码后台给具名控件赋数据源和注册事件。修改控件名称、事件签名或日志滚动单位时，要同步 MainWindow.xaml.cs 与 MainWindow.Logs.cs。
