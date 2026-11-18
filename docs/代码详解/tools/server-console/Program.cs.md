# Program.cs 代码详解

源码：[Program.cs](../../../../tools/server-console/Program.cs)

## 入口和工作目录

Program.Main 是 WPF 控制台入口，带 STAThread 以满足 WPF 与剪贴板线程要求。命令行只接受仓库路径，或仓库路径加 --check；参数形状不符时打印用法并返回 2。

入口把仓库路径转为绝对路径并去掉末尾目录分隔符，再对大写路径的 UTF-8 内容计算 SHA-256，生成 key。该 key 同时用于工作目录级 Mutex 名称和个人偏好子目录，确保同一仓库只开一个控制台，而不同仓库互不争抢窗口或设置。

## 单实例和应用组装

Mutex 使用 Local\TGame.ServerConsole-<key>，覆盖当前 Windows 会话。没有拿到首次创建权时，检查模式写标准错误，普通模式弹出提示，返回 1。using 保证入口结束释放互斥句柄。

获得互斥后创建 Application、ConsoleSession 和 MainWindow。UserSettings 从 LocalApplicationData/TGame/ServerConsole/<key>/user.json 加载。ConsoleSession 负责服务器会话，窗口只接收这两个已经构造好的对象。

普通模式运行 app.Run(window)，返回 WPF 的退出码。退出入口时 ConsoleSession.Dispose 只释放进程句柄，服务器是否停止由用户显式命令决定，不随窗口关闭而自动 Kill。

## --check 模式

检查模式不进入长期消息循环，而是先 session.Refresh，再按 1280×800 调用窗口 Measure 和 Arrange，最后输出成功加载及页签数量。它能发现配置读取、XAML 装配和基本布局初始化失败；它不执行服务器启动、服务连接或真实桌面交互测试。

## 失败处理

组装或运行中的异常统一写 Console.Error。普通模式额外显示错误 MessageBox，检查模式避免弹窗，最终返回 1。这个边界让脚本既能根据退出码判断失败，也能获取可读原因；更具体的路径、配置和进程错误由内部对象抛出。
