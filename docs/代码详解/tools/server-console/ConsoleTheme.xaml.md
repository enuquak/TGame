# ConsoleTheme.xaml 代码详解

源码：[ConsoleTheme.xaml](../../../../tools/server-console/ConsoleTheme.xaml)

## 共享资源

这是 MainWindow 和 StopServersWindow 共用的 ResourceDictionary。Ink、Muted、Line、Accent 分别表示主要文字、辅助文字、边界线和绿色强调色。它只提供视觉资源，不保存运行状态或业务配置。

Button、TextBox、CheckBox 使用无 x:Key 的隐式样式，作用于加载该字典后对应类型的控件。调用处仍可通过局部 Background、Foreground、Padding 等属性覆盖默认值，例如停止确认按钮使用红色。

## 按钮模板

Button 默认白底、1 像素边框、11×6 内边距和手形鼠标。ControlTemplate 以圆角 Border 包裹 ContentPresenter，使用 TemplateBinding 传递背景、边框、厚度和内边距，所以调用处设置不会被模板忽略。

ContentPresenter 水平和垂直居中，并开启 RecognizesAccessKey。触发器在悬停和键盘聚焦时把边框改为 Accent；按下时降低到 0.76 透明度；禁用时降为 0.46。键盘焦点也有可见反馈，不仅依赖鼠标悬停。

## 输入与勾选

TextBox 默认主文字颜色、白色背景、边框和 8×4 内边距，垂直内容居中；具体输入框可以覆写高度、字体、是否只读和滚动条。主窗口深色命令输出区就是局部覆写的例子。

CheckBox 统一垂直排列和前景色，保留 WPF 自带勾选模板和行为。这里没有重写输入框或复选框的完整模板，因此选择、焦点和输入行为仍由标准控件处理。

## 使用与变更影响

主窗口通过 MergedDictionaries 合并它，再定义局部菜单/页签/日志行样式；关闭对话框直接把它设为窗口资源。资源键或共享模板的修改会影响两个窗口，特定页签颜色应放在 MainWindow 的局部样式，不应误改所有按钮。
