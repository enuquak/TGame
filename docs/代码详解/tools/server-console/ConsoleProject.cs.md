# ConsoleProject.cs 代码详解

源码：[ConsoleProject.cs](../../../../tools/server-console/ConsoleProject.cs)

## 配置职责

本文件定义控制台自己的共享项目配置，与服务器 server.yaml 分工明确。ConsoleProject 只有 EnvironmentName 和有序 Commands；它决定左侧环境标题及命令菜单，不负责进程数量、连接拓扑或中间件地址。ConsoleSession 从 tools/server-console/project.json 加载它。

ConsoleAction 枚举列出 Start、Stop、ExportProtobuf、Build、BuildAndRestart。MainWindow 按 Commands 原始顺序映射为中文名称、图标和执行委托，因此配置中的顺序直接影响界面顺序。

## 加载过程

Load 为本次读取创建 JsonSerializerOptions：字段使用 camelCase；UnmappedMemberHandling.Disallow 拒绝未知字段；JsonStringEnumConverter 只允许枚举名称，不接受整数值。File.ReadAllText 的内容被反序列化为 ConsoleProject 记录。

反序列化后继续校验：记录不能为 null，环境名不能空白，命令必须至少一项，并且去重前后数量必须相同。这样重复菜单项和空菜单在进入 UI 前就被拒绝。合法实例原样返回，不重新排序命令。

## 失败与调用链

JSON 语法、未知字段和无效枚举引发的 JsonException 包装为包含配置路径的 InvalidDataException；业务校验也抛 InvalidDataException。文件不存在或读取权限问题保留 I/O 异常，由入口 Program 统一展示启动失败。

此类不保存静态配置实例，也没有运行期写入功能。UserSettings 保存个人显示偏好，ConsoleProject 保存版本控制中的共享命令入口；两者不能互相覆盖。ConfigChecks 验证环境名称、命令顺序以及未知命令拒绝。
