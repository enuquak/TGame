# ConfigChecks.cs 代码详解

源码：[ConfigChecks.cs](../../../../../tools/server-console/tests/ConfigChecks.cs)

## 测试对象

ConfigChecks.Run 检查控制台项目配置 ConsoleProject 和个人偏好 UserSettings，不检查服务器 YAML。所有文件写在传入 checkout 的 build/config-check 下，避免改动真实项目配置或用户配置目录。

## 项目配置断言

测试先写入中文环境名“联调环境”，以及 Build、Start 两条有序命令。ConsoleProject.Load 后要求环境名原样保留，Commands 顺序与输入相同。这确保 JSON 枚举转换与界面菜单排序一致。

接着把命令换成未知的 EraseDatabase，要求 Load 抛 InvalidDataException。若正常返回，测试主动抛异常，而非把未知命令静默忽略。

## 用户配置断言

测试准备 user-a/user.json 和 user-b/user.json 两条路径。从不存在的第一条加载默认值，使用 ServerProcess.FormatName 生成 GM1，将它设为 SelectedRole，修改宽度，写入关键词 ping、仅匹配开启、跟踪关闭的 LogPreference，再 Save。

重新加载第一条后检查选中项、宽度和记录值相等；加载第二条应仍无选中项，且第二条文件不存在。这同时验证往返序列化、路径隔离，以及 Load 默认值不会偷偷建立文件。

## 损坏文件边界

最后直接把第一条文件写成坏 JSON，再次 Load。必须返回带 Warning 的默认配置，而且磁盘原文仍为坏内容。这个检查防止加载阶段覆盖用户的可诊断原文件。

Run 成功才打印 PASS。它使用抛异常的直接断言，无测试框架依赖；由入口的失败处理决定退出码。当前断言没有覆盖 Save 在 Warning 状态下生成 .bak 的完整替换流程，也不验证真实 WPF 窗口尺寸恢复。
