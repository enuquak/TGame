# 独立服务器进程脚本

用户要求项目能够独立启动、正常停止和强制结束服务器，不依赖服务器控制台；各类型有独立启动脚本，StartAll 按配置数量逐个调用。运行目录创建集中在脚本中，相对路径放在配置文件，Program.cs 不再接收目录参数。

## 实现选择

- 采用项目约定的 BAT 入口和 PowerShell 7 实现，复用 `Import-ServerTypes.ps1`、`ServerGroup`、`ServerRunFiles`；不引用控制台工程。
- 不另建 C# 管理程序，也不让脚本启动或调用控制台，避免增加构建依赖。
- 入口为 `tools/scripts/StartAll.bat`、`Start<Type>.bat`、`StopAll.bat`、`KillAll.bat`；同名 PowerShell 入口共用 `Manage-Servers.ps1` 和 `Server-ProcessFunctions.ps1`。

## 行为

- 新运行组默认编译 App，StartAll 按 `code/server/config/processes.json` 展开数量并逐个调用对应类型脚本，每次指定启动槽位。独立调用类型脚本默认新增一个实例。`-NoBuild` 使用已有编译产物。
- YAML 从 `TGAME_SERVER_CONFIG` 选择，未设置时使用本地开发配置。每轮保存 YAML、进程数量和可执行文件快照，确保运行时门槛与启动数量一致。
- 同一仓库只管理一个当前脚本进程组。重复启动保留存活进程、按当前快照补齐缺失进程；全部退出后创建新一轮。
- 正常停止使用已有的 PID 加启动时间停止信号，等待退出；超时返回失败，不自动强退。强退入口终止当前脚本进程组。
- 用独占文件锁串行化管理操作；通过每个启动槽位的独立 apphost 完整路径识别进程，持有进程句柄后操作，避免误伤同名进程。启动槽位名仍由 `ServerProcess.FormatName` 生成，实际实例名由 ZooKeeper 分配。
- `code/server/config/runtime.json` 的 `runDirectory` 配置运行根目录，默认 `logs/server-runs`，相对工作目录解析。脚本固定工作目录为仓库根目录；每轮目录路径写入可执行快照的 `config/runtime.json`。`New-ServerRunDirectory.ps1` 统一创建，Program.cs 仅接收类型并读取配置，不创建目录。
- 构建快照位于 `build/server-runs/`。不自动删除历史文件；与控制台运行组分开管理。控制台同步改用单参数入口并通过目录脚本创建自己的运行目录。
- 启动命令报告进程已拉起，实际就绪以服务器日志中的 `PROCESS_READY` 为准。

## 验证边界

只做 App/控制台编译、脚本解析、BAT 退出码及隔离假进程检查：按类型启动、重复启动、补起、单参数、配置路径、正常停止、停止超时、强退、同名进程隔离、路径含空格。不会启动真实服务器组、连接 ZooKeeper 或运行完整测试。
