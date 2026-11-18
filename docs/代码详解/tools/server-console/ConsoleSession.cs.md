# ConsoleSession.cs 代码详解

源码：[ConsoleSession.cs](../../../../tools/server-console/ConsoleSession.cs)

## 职责与边界

ConsoleSession 是一个仓库工作目录对应的控制台会话，负责编译、导出协议、建立运行组、补起进程、恢复会话和关闭指定进程。它管理的是 ServerInstance 集合；单个进程的 PID 校验、日志回放和停止协议由 ServerInstance 实现。WPF 的布局和交互不进入这个类。

构造函数先把 repoRoot 规范为绝对路径，再加载 tools/server-console/project.json。sessions 固定指向 logs/tools/server-console，logRoot 指向 logs/server。sessionName 表示已经打开的运行组，busy 用于拒绝重叠命令。Output 传递操作文本，InstancesChanged 仅在启动计划改变后通知界面重建页签。

## 配置选择与恢复

没有 current 指针时，LoadSelectedConfiguration 优先读取 ServerEnvironment.Config 所代表的 TGAME_SERVER_CONFIG；空值时使用 YamlConfiguration.ConfigurationPath，即 code/server/config/server.yaml。相对路径以仓库根目录解析。ServerGroup.Read 从同一个 YamlConfiguration 的 processes 节点展开实例，所以环境配置与启动数量不会分别选择来源。

存在 current 时先验证目录名只含 ASCII 字母、数字或连字符，再检查 console 和动态身份标记。恢复分支直接读取该运行目录中的 server.yaml，完全不重新选择环境变量或源码配置。CreateInstances 先按保存的计划建立槽位，Open 再让各 ServerInstance 恢复真实身份。

## 一轮启动如何发生

StartAsync 分三种情况：全部存活时仅报告；部分缺失时针对缺失槽位调用 StartInfo 和 Launch；全部缺失时先编译再建立新组。BuildAsync 的 restart 参数决定编译后是否换组，ExportAsync 分别调用 Engine 和 Shared 的 Protobuf_Compile。

StartNewRun 在编译成功后只加载一次选定 YAML，并从中读取 ServerGroup。它比较计划名称与现有 SlotName，只有数量或排列改变才重建实例集合。UTC 时间戳生成组名；运行记录放在 logs/tools/server-console/<组名>，可执行快照放在 build/server-console/runs/<组名>。

PrepareSnapshot 复制构建输出，随后用本次加载的配置覆盖 snapshot/config/server.yaml。SaveSnapshot 已展开所有 include，并把 runDirectory 改为相对于仓库根目录的本组目录。最后按各槽位的 ExecutableName 复制独立 apphost。运行目录脚本接收这份 YAML；脚本建目录后，控制台把同一文件复制到 runDir/server.yaml，两个入口具有一致内容且无需原 include 文件。

完整快照、运行标记和全部 ProcessStartInfo 都在停止旧组之前准备。之后才并行停止旧进程，必要时释放旧实例集合，用临时文件加 File.Move 更新 current，打开新组并逐个启动。旧二进制快照清理失败只报告提示，不撤回已经启动的新组。

## 子进程与异常

StartInfo 要求二进制快照内存在 config/server.yaml，并要求运行记录内存在完整 server.yaml。它只传进程类型参数，不指定最终实例编号；TGAME_SERVER_CONFIG 指向运行记录快照，日志根由 EngineEnvironment.LogRoot 指定。缺少快照时明确要求编译并重启。

NewProcess 统一设置仓库工作目录、禁用 ShellExecute、隐藏窗口，并移除旧数据库密码覆盖变量。Dotnet 与 PrepareRunDirectory 通过 RunProcess 执行；标准输出和标准错误同时转发，退出码非零转换为操作失败。Command 用 finally 恢复 busy，失败不会把会话永久锁住。

Dispose 仅释放各实例持有的进程句柄，不停止服务器。控制台关闭后仍可通过磁盘记录重新附加。编译失败不会停旧组；真正启动过程中部分进程失败则保留运行组供后续检查和补起，代码没有伪造整体启动成功。
