# TGame

以游戏服务器开发为主，配套一个在浏览器中运行的简易客户端，用于验证服务器功能和游戏逻辑。

## 项目文件结构

目录职责见 [项目文件结构](docs/项目文件结构.md)。Codex 日常开发可打开 `code/`；项目工具位于 `tools/`，策划源文件位于 `design/`。

## Ping/Pong 联通验证

在项目根目录运行以下命令。第一条启动云测试环境的 SSH 隧道，第二条编译 Protobuf 协议并启动 Gate、GM、Login、Base、Scene、Router、DBMgr、Profile 各一个本地进程。

```powershell
.\tools\scripts\Start-EnvironmentTunnel.bat
.\tools\scripts\Test-PingPong.bat
```

需要仓库指定的 .NET SDK、PowerShell 7.4+、本机 ASP.NET Core 开发证书（可用 `dotnet dev-certs https` 创建）及 MongoDB、Redis 测试账号密码。设置 `TGAME_MONGO_TEST_PASSWORD`、`TGAME_REDIS_TEST_PASSWORD`，或使用本机 `.local/secrets/cloud.env` 中的 `MONGO_TEST_PASSWORD`、`REDIS_TEST_PASSWORD`。首次构建需还原 NuGet 包。每个进程监听随机回环端口，在 ZooKeeper 的 `/tgame/test` 下注册本轮专用临时节点并读取地址。测试客户端用同一条 WSS 连接发送二进制 Protobuf，并校验本机 Gate 证书指纹。

验收程序跑两轮常规探针，按请求 ID 核对每一跳的收发及复用的 9 条 gRPC 双向流。普通 DBMgr 消息走 Router，存盘探针走 Base 的专线并执行 MongoDB `ping`。协议源文件见 [pingpong.proto](code/shared/protocols/pingpong.proto)。

每个服务进程按类声明上的 `SinglePointFor` 标注创建本进程的 SinglePoint。`SINGLE_POINT_INIT` 记录进程类型、实际类名和 EntityId。八个 SinglePoint 会分别收到定向探针，五种 Game 进程和 GM 会核对 Router 的位置登记。

Profile 验收使用本轮 Base 的唯一 EntityId。Base 经 Router 发布显示名和称号，Scene 首次拉取读回 MongoDB，Login 再次拉取命中 Redis，Base 撤回后 Scene 查到 Missing。验收核对调用方实际收到的摘要、两跳收发记录及存储侧日志。见[摘要协议](code/shared/protocols/profile.proto)、[测试存储地址](code/server/config/storage.test.yaml)和[云主机容量评估](docs/环境配置落地记录.md#profile-存储容量评估)。

服务进程直接写入 `logs/server/<进程类型>Server/`，例如 `logs/server/BaseServer/` 和 `logs/server/GMServer/`。每行是一条 JSON 事件，可按 `runId`、`pid`、请求 `id` 查询。每个实例使用 `<运行ID>-<PID>-yyyyMMddHH.log`，按服务器本地整点滚动，同一小时约满 10 MiB 后续写到 `_001.log`、`_002.log`。文件切换发生在下一条事件到来时，单条事件完整写入。历史分片保留。手动启动时从仓库根目录运行，或以 `TGAME_SERVER_LOG_ROOT` 指定服务日志根目录。

验收材料在 `logs/tools/pingpong/<运行ID>/`。`result.log` 汇总链路和协议验证结果，`server-logs.json` 定位本轮服务日志，`Client.log` 是测试客户端输出。服务进程的致命异常以 `PROCESS_FAILED` 记录，启动器捕获的原始标准错误保存在验收目录的 `<角色>.stderr.log`。成功末行为 `PASS processes=8 grpc_links=9 wss_sessions=1 rounds=2 pong=25 mongo_ping=2 gm_http=5`。失败返回非零退出码。已有旧版验收目录仍可通过 `Test-PingPongLogs.ps1 -RunDirectory <目录>` 复核。

默认在验收后停止本轮进程；传入 `-Hold` 可保留进程到按回车时。自动化使用 `pwsh -NoProfile -File ./tools/scripts/powershell/Test-PingPong.ps1`。测试覆盖固定八个实例的启动发现、消息路径、GM 指令和 Profile 摘要读写，动态扩缩容及玩家完整存盘属于后续验证范围。

### GM 管理入口

本地 GM 监听随机回环端口。管理工具调用 `POST /gm/commands/ping/{entityId}`，携带 `Authorization: Bearer <token>`。目标使用 Router 已登记的 32 位十六进制 EntityId。返回的 `requestId` 可关联 GM 审计和 gRPC 收发日志。

自动验收默认生成一次性随机令牌。手动验收时先设置 `TGAME_GM_TOKEN`（至少 32 个字符的随机令牌）和 `TGAME_GM_OPERATOR`（该凭据对应的操作员），以 `-Hold` 运行脚本，从控制台取得 GM 地址。用 `Read-PingPongLog.ps1 -RunDirectory <本轮验收目录> -Role Scene -Event SINGLE_POINT_INIT` 读取目标 EntityId。在另一终端中使用相同令牌执行：

```powershell
Invoke-RestMethod -Method Post -Uri "$gmAddress/gm/commands/ping/$entityId" -Headers @{ Authorization = "Bearer $env:TGAME_GM_TOKEN" }
```

有效指令返回 200，响应包含实际回包进程。鉴权失败返回 401，ID 格式错误返回 400，目标未登记返回 404，路由暂不可用返回 503。`logs/server/GMServer/` 中的 `GM_COMMAND` 记录执行人、目标、请求 ID 与状态码。

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
