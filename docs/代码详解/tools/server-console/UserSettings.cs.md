# UserSettings.cs 代码详解

源码：[UserSettings.cs](../../../../tools/server-console/UserSettings.cs)

## 保存哪些数据

UserSettings 保存本机用户的窗口大小、最大化状态、上次选中页签及每个实例的日志偏好。LogPreference 是 Keyword、MatchesOnly、Following 三个值组成的记录。Logs 以实例显示名为键；Width、Height 为恢复窗口提供候选尺寸，最终合法化由 MainWindow.RestoreWindow 完成。

path 是实际落盘位置，只能由 Load 设置。Warning 用 JsonIgnore 排除序列化，表示本次加载曾遇到坏文件或无法读取的文件；它不是用户配置。静态 Json 仅共享无状态序列化规则：camelCase、缩进和允许中文直接输出。

## 加载与隔离

Load 尝试反序列化整个 JSON，并检查 Logs 不为 null、各条偏好的 Keyword 不为 null。文件或目录不存在时正常返回默认设置，不立即建立文件。其他 I/O、权限和 JSON 错误则返回带 Warning 的默认设置，同时保留原文件供排查。

无论采用读到的内容还是默认值，最后都把传入路径保存到实例。Program 按仓库路径哈希建立 LocalApplicationData/TGame/ServerConsole/<key>/user.json，因此不同仓库工作目录的个人偏好不会共享。ConfigChecks 也用两个路径验证这种隔离。

## 保存与恢复坏文件

Save 先创建父目录，将当前配置写入 path.tmp。若 Warning 非空且原文件仍存在，File.Replace 用新文件替换原文件，并把原内容保存到 path.bak；否则 File.Move 覆盖目标。写入成功后才清空 Warning。

临时文件减少读取到半份 JSON 的机会，备份策略避免第一次成功保存直接丢掉坏配置。Save 没有吞掉 I/O 异常；MainWindow.SaveSettings 负责记录无法保存的错误。Load 也不会因为碰到坏文件就自动覆盖它。

## 与界面的关系

MainWindow 读取 Warning 后打开命令输出并提示用户，恢复尺寸时检查有限数值和工作区边界。页签切换保留运行期滚动位置，但这里只持久化关键词、筛选、跟踪与窗口信息；日志正文和服务器生命期状态从实际文件恢复，不写入个人设置。
