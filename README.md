# TGame

以游戏服务器开发为主，配套一个在浏览器中运行的简易客户端，用于验证服务器功能和游戏逻辑。

## 项目文件结构

目录骨架已创建，目录职责见 [项目文件结构](docs/项目文件结构.md)。Codex 日常开发可打开 `code/`；项目工具位于 `tools/`，策划源文件位于 `design/`。当前尚未创建游戏源码工程。

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
