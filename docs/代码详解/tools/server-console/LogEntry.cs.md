# LogEntry.cs 代码详解

源码：[LogEntry.cs](../../../../tools/server-console/LogEntry.cs)

## 日志记录与显示控件

本文件有两个不同角色。LogEntry 是已经解析的展示记录，包含绝对行号 Number、原文 Raw、时间列 Time、事件列 Event、内容列 Content。LogText 是公开的 WPF TextBlock 子类，负责把各列中的搜索关键词高亮，供 XAML 实例化。

LogEntry 不决定服务器状态。状态判断发生在 ServerInstance.AddLine/ApplyEvent；这里的解析失败只影响显示形式，不会把进程改成失败或就绪。

## 结构化日志解析

Parse 只在原文以左花括号开头时尝试 JsonDocument.Parse。如果对象存在字符串类型的 ev 字段，就把它视作结构化日志。有效时间转换成本机时间，格式为 HH:mm:ss.fff；缺少或无法识别的时间显示破折号。

内容列依次枚举 JSON 字段，跳过 time、ev、role、pid 和 null 字段。detail 直接呈现内容，其他非空值显示成字段名=值，多个部分用空格分隔。instance 不在排除列表，因此可以在内容列保留真实进程身份。

JSON 语法失败、没有合适事件字段或普通文本都退回一条“输出”记录，内容为完整原文。Raw 始终保留，右键复制和 Ctrl+C 可取得未经展示加工的日志。

## 搜索与颜色

Matches 在时间、事件和内容三列进行 OrdinalIgnoreCase 子串搜索；不重新搜索原始 JSON 元数据。EventColor 检查事件名中是否包含大小写精确的 FAILED 或 ERROR，有则为 LightSalmon，否则为 PaleTurquoise。这个配色规则只用于展示，不替代结构化事件常量驱动的进程状态机。

## LogText 的依赖属性

ValueProperty 与 KeywordProperty 都注册 Changed 回调。绑定值或关键词任一变化时，回调把 null 视为空串，执行不区分大小写的查找。

没有匹配时，如果此前创建过高亮 Inline，先清空它们，然后直接赋 Text；highlighted 记录当前是否处于 Inline 模式。存在匹配时清空旧 Inline，逐段添加普通 Run 与黄底黑字的匹配 Run，最后补上尾段。每次从上一个匹配末尾继续，因此展示的是不重叠匹配。

MainWindow.xaml 通过 ListBox.Tag 传入当前关键词，各个 LogText 的隐式样式绑定该值。列表使用容器回收，Changed 必须清理上次行留下的 Inlines，才能避免滚动后把旧高亮带到新记录。
