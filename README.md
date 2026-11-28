# TGame

以游戏服务器开发为主，配套一个在浏览器中运行的简易客户端，用于验证服务器功能和游戏逻辑。

## 项目文件结构

目录职责见 [项目文件结构](docs/项目文件结构.md)。Codex 日常开发可打开 `code/`；项目工具位于 `tools/`，策划源文件位于 `design/`。

逐文件说明见 [全项目手写源码详解](docs/代码详解/README.md)。按当前结构阅读代码请从 [代码 Review 路径](docs/代码Review路径.md) 开始；历史阅读确认与准备步骤见 [代码 Review 进度](docs/代码Review进度.md)，历史验证范围见 [2026-11-17 完整测试记录](docs/2026-11-17完整测试记录.md)。

## VS Code 开发

使用 VS Code 打开仓库根目录，并安装 C# 与 C# Dev Kit 扩展。工作区默认加载 [TGame.slnx](TGame.slnx)，仅包含正式的源码、测试与工具工程，避免自动发现 `build/` 下的临时测试副本。新增正式工程时，通过 `dotnet sln TGame.slnx add <工程路径>` 加入解决方案。

若更新 .NET SDK 后出现 `CS0518`（连 `System.Object` 都无法识别），先执行 `dotnet build TGame.slnx` 检查真实编译结果；编译通过时，在命令面板执行 `Developer: Reload Window`，让 C# 扩展重新加载 SDK 和工程引用。

## 查看提交差异

在 GitHub 切换到事项分支后，打开 **Commits** 并选择某次提交，即可查看该次提交的文件增删。要查看分支尚未合入 `main` 的全部改动，使用 **Compare**，将 `base` 设为 `main`，`compare` 设为事项分支。分支合并后，单次提交的 diff 仍可查看。

## 独立起停脚本

服务器可以完全通过项目脚本运行，无需打开或编译服务器控制台。新工作区先执行 `dotnet restore code/server/engine/TGame.Server.Engine.csproj` 准备 YAML 依赖。运行 `tools/scripts/Start-EnvironmentTunnel.bat`，确保 ZooKeeper 可达；然后双击以下 BAT，或在仓库根目录执行：

```powershell
.\tools\scripts\StartAll.bat                 # 按 process.yaml 的数量启动各类型实例
.\tools\scripts\StartRouter.bat              # 新增一个 Router 进程
.\tools\scripts\StartScene.bat               # 新增一个 Scene 进程
.\tools\scripts\StopAll.bat                  # 正常停止，默认等待 10 秒
.\tools\scripts\KillAll.bat                  # 强制结束脚本管理的当前进程组
```

每种类型都有独立入口：`StartGate`、`StartLogin`、`StartBase`、`StartScene`、`StartRouter`、`StartDBMgr`、`StartProfile`、`StartGM`。各 BAT 调用同名 PowerShell 脚本；`StartAll.ps1` 的共用实现读取 [server.yaml](code/server/config/server.yaml) 及 include 的 `processes`，直接按数量调用共用启动函数。新运行组默认只编译服务器 App；`-NoBuild` 使用已有产物。已有进程组继续使用本轮可执行文件，重复 `StartAll` 只补齐缺失槽位，单类型入口每次新增一个实例。

[server.yaml](code/server/config/server.yaml) 的 `runDirectory` 配置运行记录根目录，必须是相对仓库根目录的路径，默认 `logs/server-runs`。脚本固定工作目录并创建本轮子目录，通过 `TGAME_SERVER_RUN_DIRECTORY` 传给服务器；`TGAME_SERVER_CONFIG` 直接指向源配置。服务器不创建运行目录。固定管理指针及互斥锁保存在 `logs/tools/server-scripts/`，指针记录本轮相对运行目录；补起和停止先定位原组；补起读取源配置，停止不依赖配置。修改源 runDirectory 在创建新组时生效。

每轮可执行文件副本保存在 `build/server-runs/`，不复制或保存环境 YAML。服务器在后台运行，真正就绪以 `logs/server/` 中的 `PROCESS_READY` 为准。脚本进程组与控制台进程组分别管理，`StopAll` 和 `KillAll` 仅操作脚本组。正常停止超时返回非零退出码，不自动强退；可用 `-TimeoutSeconds 30` 调整等待时间。自动化建议直接调用 `pwsh -NoProfile -File tools/scripts/powershell/StartAll.ps1`；调用 BAT 时设置 `TGAME_NONINTERACTIVE=1` 可避免失败后等待按键。

不连接外部服务的脚本检查：`pwsh -NoProfile -File tools/scripts/powershell/Test-ServerScripts.ps1`，使用隔离假进程验证起停、退出码和进程识别。

## 服务器控制台

Windows 上双击 `tools\scripts\Start-ServerConsole.bat`，或在项目根目录运行它。进程基础框架使用本机游戏进程及下文的 ZooKeeper 隧道。打开窗口和查看已有日志可以离线进行。使用 `-Check` 可验证控制台能否加载。

- **启动服务器** 按 [进程数量配置](code/server/config/process.yaml) 拉起各类型的全部实例。再次执行会补起缺少的进程，并直接读取源配置。配置格式、实例命名与就绪条件见 [服务器组配置](docs/服务器组配置.md)。双击 **关闭服务器** 后勾选本次要关闭的实例。关闭控制台窗口后服务器继续运行，下次打开可继续管理。
- **导出 Protobuf** 生成协议 C# 源码。**编译服务器 C#** 构建服务端工程。**编译并重启** 在构建成功后切换到新一轮进程，编译失败时可在命令输出中查看原因。
- 右栏为每个启动槽位创建独立页签，认领身份后显示实际进程名。可以滚动或搜索进程名、PID 切换实例；搜索偏好和选中项按稳定槽位保存。绿点表示已收到当前进程的 `PROCESS_READY`，此后依赖断线或日志读取积压不撤销就绪；失败、停止和退出仍更新对应状态。
- 各页签保留最近 1000 行。按 `Ctrl+F` 搜索，`F3` 和 `Shift+F3` 跳到下一条、上一条匹配日志，也可勾选“仅匹配行”来过滤显示。清空显示后继续接收新日志，磁盘上的滚动日志可用于复盘。

### 项目配置与用户配置

| 范围 | 保存位置 | 内容 |
| --- | --- | --- |
| 项目 | [控制台配置](tools/server-console/project.json) | `environmentName` 是左栏显示的环境名称。`commands` 决定可用命令和显示顺序。可选值为 `Start`、`Stop`、`ExportProtobuf`、`Build`、`BuildAndRestart`。 |
| 项目 | [服务端进程数量](code/server/config/process.yaml) | 决定脚本及控制台初始启动数量，并作为服务器首次连接门槛。 |
| 用户 | `%LOCALAPPDATA%\TGame\ServerConsole\<工作目录标识>\user.json` | 关闭窗口时保存尺寸、最大化状态、当前页签，以及各进程的搜索词、仅匹配行和跟踪最新选项。每个 Windows 账户和工作目录分别保存。 |

项目配置随 Git 提交，下次打开控制台时生效。个人设置在本机自动生成。实际进程状态由控制台从运行实例和日志重新读取；独立存储联调所需凭据取自环境变量或本机秘密配置，不写入仓库。

完整自动化验收通过 `pwsh -NoProfile -File tools/scripts/powershell/Test-ServerConsole.ps1` 运行。执行前须说明范围与预计耗时并获得明确同意，再启动测试隧道。脚本在 `build/console-check/` 中建立独立工作目录，验证起服、选关、补起、重新接管以及编译成功和失败时的重启行为。

## 服务器环境

服务端通过 `TGAME_SERVER_CONFIG` 显式选择 YAML 文件；未选择或基础配置不完整时拒绝启动。进程基础框架要求环境名、相对运行目录、进程数量与拓扑以及完整 `zooKeeperOptions` 配置，`loggingMinimumLevel` 可选，默认 `INFO`。启动不要求数据库地址、账号或密码；独立存储工具使用时自行校验所需配置与凭据。

[server.yaml](code/server/config/server.yaml) 是本地默认入口，脚本与控制台优先使用 `TGAME_SERVER_CONFIG` 指定的入口。它通过 `include: [ops, process]` 引用同目录的两个文件；各文件独立解析后合并顶层字段，重名直接失败，YAML 锚点和别名只在单文件内有效。Engine 的 `ServerConfiguration` 一次解析、严格绑定并校验核心配置、数量和拓扑；宿主将该实例及其持有的 `Group`、ZooKeeper/ID 选项注册为 IoC 单例，不另建宿主配置对象，也不自动热重载。启动器直接传递源配置路径，重新接管和补起也读取源配置，不保存独立 YAML 快照。

总入口示例（其引用文件也必须存在）：

```yaml
include: [ops, process]
environment: test
loggingMinimumLevel: DEBUG
runDirectory: logs/server-runs
```

[ops.yaml](code/server/config/ops.yaml) 包含 MongoDB、Redis、ZooKeeper 的连接参数及密码变量名。基础框架不建立存储连接。

主动连接规则由 [process.yaml](code/server/config/process.yaml) 的 `connections` 声明，必须无环。运行时持续发现在线实例；同文件的 `processes` 决定初始启动数量和各类型首次连接门槛，数量不构成连接上限。

## 服务器运行与验证

当前服务器提供 ZooKeeper 占号和发现、进程连接、基础 RPC、日志和 ID。进程启动不创建实体、不登记或路由实体，也不初始化 DBMgr/Profile 业务。独立存储工具和共享业务协议继续保留。

以下是短时本地检查入口，不启动服务器组，也不连接外部 ZooKeeper 或数据库。按改动选择所需检查：

```powershell
# 基础节点、握手、就绪、请求关联、超时与取消。
dotnet run --project code/tests/engine/TGame.EngineTests.csproj -- --engine

# 控制台 ProcessReady 状态、身份跟踪、恢复与精确停止。
dotnet run --project tools/server-console/tests/ServerConsole.Check.csproj -- --state-only
```

测试无参数时运行 `--engine`，`--node` 只运行节点通信专项。动态连接、日志、配置、ID 和独立存储检查见[服务器启动与动态发现](docs/服务器组配置.md)、[代码 Review 路径](docs/代码Review路径.md)及[统一 ID 生成](docs/雪花ID.md)。外部服务联调或完整控制台验收须先说明范围与预计耗时，获得明确同意后执行。历史测试记录不代表当前版本已完成联调。

宿主启动依次完成日志 → 配置 → 构建 IoC 根容器 → ZooKeeper 会话与空地址临时占号 → ID → 监听及主动依赖连接 → 地址发布 → `PROCESS_READY`。首次启动要求每种主动目标类型达到配置数量，`FirstLinks` 达标后完成一次，`ServerNode.Ready` 由其完成状态派生并保持为真；无主动依赖的 Router 可直接就绪，不等入站。依赖断线只影响对应连接与 RPC，发现和重连持续运行。连接握手只交换进程身份。

当前所有监听地址均为本机 HTTP，不加载或管理 TLS 证书。Gate 使用 HTTP/1.1，尚未接入客户端业务接口；其余进程使用 HTTP/2 承载内部 gRPC。

服务端入口 [Program.cs](code/server/app/Program.cs) 只接受 `<进程类型>`，例如 `Scene`；运行目录优先读取 `TGAME_SERVER_RUN_DIRECTORY`，未设置时使用源配置的 `runDirectory`，相对进程工作目录解析。脚本与控制台均把工作目录设为仓库根目录，并预先创建本轮运行目录。类型启动后通过 ZooKeeper 自动抢占从 1 开始的空闲实例编号，统一生成为 `Scene1`、`Scene2` 等名称。空地址节点不参与服务发现，但可证明尚未发布地址的主动连接方持有合法身份。强制终止后，旧节点在会话过期时删除；期间新进程可抢占其他空闲编号。

手动起服从仓库根目录执行；依赖类型应分别启动，例子和默认连接关系见[服务器启动与动态发现](docs/服务器组配置.md)：

```powershell
dotnet build code/server/app/TGame.Server.csproj
$env:TGAME_SERVER_CONFIG = (Resolve-Path code/server/config/server.yaml).Path
pwsh -NoProfile -File tools/scripts/powershell/New-ServerRunDirectory.ps1 -Configuration $env:TGAME_SERVER_CONFIG
dotnet build/bin/TGame.Server/debug/TGame.Server.dll Router
```

服务进程认领身份前，将日志写入本轮运行目录的 `startup/<PID>-<启动UTC ticks>/`，因此配置加载、ZooKeeper 初始化和占号失败也可在控制台查看。未指定 `TGAME_SERVER_RUN_DIRECTORY` 时，启动日志保存在服务日志根目录的同名子目录。认领身份后写入 `logs/server/<实例名称>/`，例如 `logs/server/Login1/`。文件名使用本机时间 `yyyyMMddHHmm.log`：自然跨小时切分时分钟固定为 `00`，例如 `202607112300.log`；约满 10 MiB 时按实际分钟分片，例如 `202607112317.log`，同一分钟再次分片使用 `_001.log`、`_002.log`。重启后追加到该小时最新的可用分片。每行是一条 JSON 事件，控制台通过 PID、角色、实例和记录时间筛选当前进程的日志。`TGAME_SERVER_LOG_ROOT` 可指定服务日志根目录。控制台依据 INFO 级别的 `PROCESS_READY` 记录启动完成，最低级别设为 `ERROR` 时无法准确显示就绪。

独立引擎项目 [TGame.Server.Engine.csproj](code/server/engine/TGame.Server.Engine.csproj) 承担进程拓扑、gRPC 接入、连接管理、服务发现、基础 RPC、日志与 ID；可以单独构建，不引用宿主或业务 Shared。宿主 [TGame.Server.csproj](code/server/app/TGame.Server.csproj) 显式引用 Engine 与 Shared，使用 .NET 内置 IoC 容器组装进程服务；ZK、ID 和节点使用共享实例，使用方通过构造函数接收依赖。配置显式加载并注入；日志在配置加载前创建，注册到容器共享，由宿主最后释放以记录启动与清理失败。

基础 RPC 通过 `ServerNode.CallPeerAsync<TReply>` 直接向已连接的进程实例发送请求，支持请求关联、错误、超时、取消和断线。当前没有业务处理入口，收到请求返回 `Unimplemented`；Router 不转发请求到第三个进程。连接服务和信封定义见 [transport.proto](code/server/engine/protocols/transport.proto)，通过 `Any` 承载负载。引擎协议生成到 `code/server/engine/generated/protocols/`，业务 [profile.proto](code/shared/protocols/profile.proto) 生成到 `code/shared/generated/protocols/`；控制台导出命令同时处理两者。

唯一 ID 通过注入的 `IdGenerator` 实例调用 `ids.GenId()` 生成，宿主显式初始化容器中的共享实例，通过同一 ZooKeeper 代理申请雪花 worker 编号；worker 编号与进程实例编号独立。基础框架用于请求和连接 ID，保留的存储工具用于缓存占用标记，详见[统一 ID 生成](docs/雪花ID.md)。各进程需统一重新构建并重启，不能混用旧实体协议与当前基础协议。

### 独立存储工具

[MongoStorageSettings](code/server/engine/configuration/MongoStorageSettings.cs) 是 Engine 中的纯配置类型；[MongoStorage](code/server/logic/storage/MongoStorage.cs) 在 logic 中负责客户端创建与连接检查。`MongoStorageSettings.Load` 要求完整有效的服务器配置，不接受只有 Mongo 节的片段；[ops.yaml](code/server/config/ops.yaml) 提供独立 MongoDB 配置示例。调用方显式提供密码，配置文件不保存密码。DBMgr 进程启动不调用这些工具，也不以数据库检查决定就绪。

[ProfileStore](code/server/logic/storage/ProfileStore.cs) 与 [StorageConnections](code/server/logic/storage/StorageConnections.cs) 保留 MongoDB/Redis 存储能力，使用时要求完整的存储字段与配置指定的凭据变量。本地示例变量为 `TGAME_MONGO_PASSWORD` 和 `TGAME_REDIS_PASSWORD`。这些工具没有进程 RPC 入口；只有独立存储操作和存储联调需要准备对应数据库及凭据。

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

当前进程角色、连接拓扑、基础通信与扩缩容连接数见 [服务器架构与进程拓扑](code/agents/服务器架构与进程拓扑.md)。以下热重载约定属于后续业务设计，当前尚未实现对应加载边界。

- 引擎层与业务层分离，引擎层常驻，不参与业务热更新。
- 核心要求是支持不重启进程的业务热重载。计划采用可回收的 `AssemblyLoadContext` 加载业务 DLL，通过 `LogicRouter` 切换业务实现。
- 通过常驻转发方法封装 `LogicRouter`，减少热更新机制对日常业务开发的影响。
- 热更新范围限定为业务函数逻辑，数据结构、RPC 协议及共享接口保持稳定。
- 服务端采用常规 CoreCLR / JIT 运行方式，不采用 Native AOT 发布。

客户端与服务器可以共享协议消息、枚举、数据定义及适合跨端使用的通用 C# 代码。游戏的权威状态和业务判定由服务器维护，客户端通过网络协议提交操作并展示结果。

## 配置数据的组织与格式约定

服务器运行 YAML 已使用统一加载和校验机制；其余配置类别的加载与热重载机制按各自后续设计确定。

| 配置类别 | 维护方式 | 产物格式 |
| --- | --- | --- |
| 策划表格配置 | 策划维护 Excel，由工具导出 | 自动生成的 C# 源码 |
| 服务器运行配置 | 按开发、测试、生产环境维护 | YAML 文件 |
| 编辑器生成的玩法配置 | 在行为树、技能等编辑器中维护 | 采用具体编辑器的原生导出格式，不强制使用 JSON |
| 运营动态配置 | 开发者在一个 C# 文件中集中维护开关及参数 | 手写的 C# 配置源码 |

两类 C# 配置编译进程序集；YAML 作为独立数据文件加载，编辑器产物由配套运行时或加载组件处理。需要运行中调整的 C# 配置值不声明为 `const`。
