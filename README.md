# TGame

以游戏服务器开发为主，配套一个在浏览器中运行的简易客户端，用于验证服务器功能和游戏逻辑。

## 项目文件结构

目录职责见 [项目文件结构](docs/项目文件结构.md)。Codex 日常开发可打开 `code/`；项目工具位于 `tools/`，策划源文件位于 `design/`。

按当前结构阅读代码请从 [代码 Review 路径](docs/代码Review路径.md) 开始；历史阅读确认与准备步骤见 [代码 Review 进度](docs/代码Review进度.md)，本轮验证结果见 [完整测试记录](docs/2026-11-17完整测试记录.md)。

## VS Code 开发

使用 VS Code 打开仓库根目录，并安装 C# 与 C# Dev Kit 扩展。工作区默认加载 [TGame.slnx](TGame.slnx)，仅包含正式的源码、测试与工具工程，避免自动发现 `build/` 下的临时测试副本。新增正式工程时，通过 `dotnet sln TGame.slnx add <工程路径>` 加入解决方案。

若更新 .NET SDK 后出现 `CS0518`（连 `System.Object` 都无法识别），先执行 `dotnet build TGame.slnx` 检查真实编译结果；编译通过时，在命令面板执行 `Developer: Reload Window`，让 C# 扩展重新加载 SDK 和工程引用。

## 服务器控制台

Windows 上双击 `tools\scripts\Start-ServerConsole.bat`，或在项目根目录运行它。开发环境使用本机游戏进程及下文的数据库、ZooKeeper 隧道。打开窗口和查看已有日志可以离线进行。使用 `-Check` 可验证控制台能否加载。

- **启动服务器** 按 [进程数量配置](code/server/config/processes.json) 拉起各类型的全部实例。再次执行会按本组配置快照补起缺少的进程。配置格式、实例命名与就绪条件见 [服务器组配置](docs/服务器组配置.md)。双击 **关闭服务器** 后勾选本次要关闭的实例。关闭控制台窗口后服务器继续运行，下次打开可继续管理。
- **导出 Protobuf** 生成协议 C# 源码。**编译服务器 C#** 构建服务端工程。**编译并重启** 在构建成功后切换到新一轮进程，编译失败时可在命令输出中查看原因。
- 右栏为每个配置实例创建独立页签。可以滚动或搜索进程名、PID 切换实例。绿点表示当前实例的 SinglePoint 已完成初始化，并且所依赖类型的全部配置实例连接齐备。关闭某个实例后，其直接邻居会显示连接不全，重新连通后恢复绿色。
- 各页签保留最近 1000 行。按 `Ctrl+F` 搜索，`F3` 和 `Shift+F3` 跳到下一条、上一条匹配日志，也可勾选“仅匹配行”来过滤显示。清空显示后继续接收新日志，磁盘上的滚动日志可用于复盘。

### 项目配置与用户配置

| 范围 | 保存位置 | 内容 |
| --- | --- | --- |
| 项目 | [控制台配置](tools/server-console/project.json) | `environmentName` 是左栏显示的环境名称。`commands` 决定可用命令和显示顺序。可选值为 `Start`、`Stop`、`ExportProtobuf`、`Build`、`BuildAndRestart`。 |
| 项目 | [服务端进程数量](code/server/config/processes.json) | 服务器和控制台共用的类型与实例数量；新运行组保存配置快照。 |
| 用户 | `%LOCALAPPDATA%\TGame\ServerConsole\<工作目录标识>\user.json` | 关闭窗口时保存尺寸、最大化状态、当前页签，以及各进程的搜索词、仅匹配行和跟踪最新选项。每个 Windows 账户和工作目录分别保存。 |

项目配置随 Git 提交，下次打开控制台时生效。个人设置在本机自动生成。测试凭据继续取自环境变量或 `.local/secrets/cloud.env`，实际进程状态由控制台从运行实例和日志重新读取。

自动化验收通过 `pwsh -NoProfile -File tools/scripts/powershell/Test-ServerConsole.ps1` 运行。请先启动测试隧道。脚本在 `build/console-check/` 中建立独立工作目录，验证起服、选关、补起、重新接管以及编译成功和失败时的重启行为。

## 服务器环境

服务端通过 `TGAME_SERVER_CONFIG` 显式选择 YAML 文件；未选择或配置不完整时拒绝启动。`ServerSettings` 读取 Profile 数据库地址、数据库名、用户、凭据环境变量名、Redis 键前缀和 ZooKeeper 注册根路径。DBMgr 继续使用下文说明的独立存储配置。

[server.local.yaml](code/server/config/server.local.yaml) 描述当前本地开发环境，沿用 SSH 隧道和已有联调账号。控制台默认选择这份配置；设置 `TGAME_SERVER_CONFIG` 后使用指定环境。启动新一组进程时，控制台将所选 YAML 保存为运行目录中的 `server.yaml`；重新接管或补起进程时复用这份快照，不受后来环境变量或源文件变化影响。其它环境提供自己的 YAML 和对应凭据，ZooKeeper 注册根路径须预先创建。

## 服务器运行与验证

Probe Ping/Pong、`/probe` 入口和专用验收工具已移除。通用 RPC、消息队列和 Profile 就绪检查可在本机运行：

```powershell
dotnet run --project code/tests/singlepoint/TGame.SinglePointTests.csproj -- --engine
```

这项检查不启动服务器组，也不连接数据库或 ZooKeeper。Profile 存储联调与控制台整组验收按需单独运行。

每个服务进程先完成引擎初始化（监听、ZooKeeper 注册和首次拓扑连接），再按 `SinglePointFor` 标注创建并初始化本进程的 SinglePoint。业务实现可选；没有注册实现的角色使用引擎 `ServerSinglePoint` 默认类。目前 DBMgr 和 Profile 提供业务子类，其余角色使用默认类。`SINGLE_POINT_INIT` 与 `SINGLE_POINT_READY` 记录初始化的开始与成功，均带实际类名和 EntityId。`LINKS_READY` 单独记录所有预期实例连接齐备。

引擎提前生成并登记本进程的 EntityId，随后创建的 SinglePoint 复用同一份身份。Router 只接受非空、无重复的 雪花 ID 实体登记；Profile 请求按 `ProcessType.Profile` 对应的连接转发，并校验调用角色及写入实体的归属。正式流程不登记或依赖固定的测试 ID。

服务进程直接写入 `logs/server/<实例名称>Server/`，例如 `logs/server/Base1Server/` 和 `logs/server/GM1Server/`。每行是一条 JSON 事件，可按 `pid`、角色、时间和请求 `id` 查询，不再包含 `runId`。每个实例使用 `<PID>-<进程启动UTC ticks>-yyyyMMddHH.log`，用启动时间区分复用的 PID。按服务器本地整点滚动，同一小时约满 10 MiB 后续写到 `_001.log`、`_002.log`。文件切换发生在下一条事件到来时，单条事件完整写入。历史分片保留。手动启动时从仓库根目录运行，或以 `TGAME_SERVER_LOG_ROOT` 指定服务日志根目录。

服务端入口为 `code/server/app/Program.cs`，只接受 `<实例名> <运行目录>`，运行目录名称不要求 GUID；例如 `Base2 <运行目录>`；实例类型与预期数量从运行目录中的 `processes.json` 快照读取。

服务端入口工程为 `code/server/app/TGame.Server.csproj`，输出 `TGame.Server.dll`。独立引擎项目 [TGame.Server.Engine.csproj](code/server/engine/TGame.Server.Engine.csproj) 承担进程拓扑、gRPC 接入、连接管理、服务发现及 SinglePoint 基础能力，使用 `TGame.Server.Engine` 命名空间。可选的业务 SinglePoint 子类与 Profile 处理位于 `code/server/logic/`；宿主统一创建引擎 `ServerNode`，提供业务类型集合并按两个阶段启动服务。Profile 通过通用 RPC 调用，其参数与写入归属校验由 `ProfileMessages` 承担，存储处理由 `ProfileServerSinglePoint` 承担。引擎可以单独构建，不引用宿主或业务实现。GM 进程使用引擎默认 SinglePoint，保留与 Router 的拓扑连接。

唯一 ID 统一调用引擎的 `idGenerator.GenId()`，使用雪花算法；ZooKeeper 自动分配进程编号，引擎与业务共用生成器。请求、实体、连接和缓存占用标记均已接入，详见[雪花 ID](docs/雪花ID.md)。

日志 JSON 格式由 Engine 中的 `TGame.Logging` 维护。连接服务与通用 RPC 帧定义在 [transport.proto](code/server/engine/protocols/transport.proto)，请求与响应通过 Protobuf `Any` 承载。`RpcDispatcher` 根据消息类型分发，`logic/messages/ServerMessages.cs` 注册 Profile 处理器。引擎协议生成到 `code/server/engine/generated/protocols/`，业务 [profile.proto](code/shared/protocols/profile.proto) 生成到 `code/shared/generated/protocols/`；控制台导出命令同时处理两者。宿主显式引用 Engine 和业务 Shared，Engine 不引用 Shared。

内部 gRPC 服务为 `tgame.transport.ServerLink`，服务发现采用固定的 `server-<实例名>` 节点；各进程须使用统一重新构建的版本，不能混用仍带运行 ID 的旧构建。控制台的“编译并重启”会统一构建、启动各角色。强制终止后若 ZooKeeper 会话尚未过期，新进程会起服失败，待旧节点消失后再启动。可运行 `dotnet run --project code/tests/singlepoint/TGame.SinglePointTests.csproj -- --registry` 检查节点冲突与正常退出后的重新注册；该检查需要 ZooKeeper 隧道。

### DBMgr 存储配置

DBMgr 使用 [dbmgr-storage.yaml](code/server/config/dbmgr-storage.yaml) 配置 MongoDB 地址、业务数据库、认证数据库、用户名和服务器选择超时。构建和发布会把它复制到程序目录的 `config/` 下；仓库提供的是本机联调值，部署时替换为目标环境配置。密码只从 `TGAME_DBMGR_MONGO_PASSWORD` 读取，不写入配置文件。

`MongoStorageSettings` 负责读取和校验配置，`MongoStorage` 负责创建连接及检查数据库响应。DBMgr 初始化会调用 `CheckDatabaseConnectionAsync`，检查成功后才标记为就绪。DBMgr 不再读取 `TestStorage`、`storage.test.yaml` 或测试密码变量。

手动启动 DBMgr 前须设置 `TGAME_DBMGR_MONGO_PASSWORD`。本机服务器控制台优先使用该变量；未设置时，由启动器将现有 MongoDB 联调凭据注入该变量。Profile 使用 `TGAME_SERVER_CONFIG` 指定的环境配置，凭据变量名由该配置声明；本地配置使用 `TGAME_MONGO_PASSWORD` 和 `TGAME_REDIS_PASSWORD`。

## 云服务器连接

统一读取本机 `$env:USERPROFILE\.ssh\config`（当前为 `C:\Users\Administrator\.ssh\config`），通过 `TGame-server` 别名连接。在项目根目录执行：

```powershell
.\tools\scripts\Connect-Server.bat -Check                         # 验证连接
.\tools\scripts\Connect-Server.bat                                # 交互登录
pwsh -NoProfile -File .\tools\scripts\powershell\Connect-Server.ps1 -RemoteCommand 'hostname && uptime'
```

连接参数、文件传输、SSH 隧道及排查方式见 [云服务器标准连接方式](docs/服务器连接.md)。

本机工具与云端基础服务已配置，访问入口、凭据位置和验收结果见 [环境配置落地记录](docs/环境配置落地记录.md)。项目统一使用 **BAT 入口 + PowerShell 7 实现**；可双击 BAT，也可从 CMD、Windows PowerShell 5.1 或 PowerShell 7 调用。运行 `./tools/scripts/Start-EnvironmentTunnel.bat` 启动环境隧道；双击启动可使用 `tools/scripts/Start-EnvironmentTunnel.bat`，窗口会保留结果。云端配置与备份维护见 [部署说明](deploy/README.md)。

**项目标准：本机访问云端数据库、中间件、管理页面及知识服务，统一走 SSH 隧道。** 这些服务的云端发布端口和本机转发端口均绑定 `127.0.0.1`，不采用公网 IP 白名单直连。云端容器之间使用内部服务地址；玩家公共入口另行规划。协作约定见 [AGENTS.md](AGENTS.md)。

## 当前技术选型

| 模块 | 技术方案 | 版本 | 说明 |
| --- | --- | --- | --- |
| 服务器 | C# 标准 .NET 服务器 | .NET 10 LTS / C# 14 | 引擎层与业务层语言统一，均使用 C# 14 |
| 客户端 | WebGUI，采用 Blazor WebAssembly | .NET 10 / C# 14 | 在浏览器中运行，定位为服务器功能验证客户端 |
| 存盘数据库 | MongoDB | 9.0.4 | 用于持久化存储 |
| 缓存数据库 | Redis | 8.10.3 | 用于缓存 |

## 服务端设计约定

进程职责、Body 与 Soul 的归属、消息路径及扩缩容时的连接数见 [服务器架构与进程拓扑](code/agents/服务器架构与进程拓扑.md)。

- 引擎层与业务层分离，引擎层常驻，不参与业务热更新。
- 核心要求是支持不重启进程的业务热重载。计划采用可回收的 `AssemblyLoadContext` 加载业务 DLL，通过 `LogicRouter` 切换业务实现。
- 通过常驻转发方法封装 `LogicRouter`，减少热更新机制对日常业务开发的影响。
- 热更新范围限定为业务函数逻辑，数据结构、RPC 协议及共享接口保持稳定。
- 服务端采用常规 CoreCLR / JIT 运行方式，不采用 Native AOT 发布。

客户端与服务器可以共享协议消息、枚举、数据定义及适合跨端使用的通用 C# 代码。游戏的权威状态和业务判定由服务器维护，客户端通过网络协议提交操作并展示结果。

## 配置数据的组织与格式约定

以下约定暂定配置的维护方式和产物格式，具体加载、校验与热重载机制在后续设计中确定。

| 配置类别 | 维护方式 | 产物格式 |
| --- | --- | --- |
| 策划表格配置 | 策划维护 Excel，由工具导出 | 自动生成的 C# 源码 |
| 服务器运行配置 | 按开发、测试、生产环境维护 | YAML 文件 |
| 编辑器生成的玩法配置 | 在行为树、技能等编辑器中维护 | 采用具体编辑器的原生导出格式，不强制使用 JSON |
| 运营动态配置 | 开发者在一个 C# 文件中集中维护开关及参数 | 手写的 C# 配置源码 |

两类 C# 配置编译进程序集；YAML 作为独立数据文件加载，编辑器产物由配套运行时或加载组件处理。需要运行中调整的 C# 配置值不声明为 `const`。
