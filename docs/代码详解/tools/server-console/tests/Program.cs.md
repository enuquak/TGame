# tests/Program.cs 代码详解

源码：[Program.cs](../../../../../tools/server-console/tests/Program.cs)

## 检查入口和执行范围

这是 ServerConsole.Check 的顶层程序。它有轻量和集成两条执行路径。--identity-only 只运行 DynamicIdentityChecks；--state-only 在临时目录依次运行 ConfigChecks、GroupConfigChecks、LogChecks，再运行动态身份检查。轻量模式不启动真实游戏服务器，不访问 ZooKeeper。

集成路径要求第一个参数是带 .console-check 标记的临时 checkout，且最多再带一个模式参数。这个标记由 Test-ServerConsole.ps1 创建，防止测试操作真实开发工作区中的服务器。ConfigChecks 和 GroupConfigChecks 先执行；--config-only 到这里返回。随后日志检查完成，再创建 ConsoleSession 并把输出转发到 Console。

## 完整集成流程

先调用 ExportAsync，检查 Engine Transport.cs、TransportGrpc.cs 和业务 Profile.cs 已生成；--export-only 在此结束。正常继续 StartAsync，等待所有实例就绪，再额外启动 Router 验证动态认领。重新创建 ConsoleSession 并刷新，要求实例 PID 和状态保持不变，证明会话重新附加正确。

随后保存原环境变量，故意将 `EngineEnvironment.Config`（TGAME_SERVER_CONFIG）改成不存在的文件，关闭并重新打开控制台，停止一个 Base，要求 Gate 保持就绪，再补起 Base。恢复必须使用运行组快照，不能重新读取这个失效入口。finally 恢复环境变量，并检查补起没有扰动其他 PID。Router 的选择性停止/补起同样要求下游 Base 保持首次启动完成后的就绪状态；停止唯一 Base 或唯一 Router 都不再等待“连接不全”。补起后等待所有进程就绪并核对 Router 的新 PID；实际链路与 RPC 恢复另由引擎专项检查覆盖。

测试接着写入带 #error 的临时源码，让 BuildAsync(restart:true) 编译失败。无论失败如何都删除该文件，然后检查旧组仍就绪且 PID 未变。最后通过 `ServerConfiguration.Read` 从完整 YAML 的 processes 节点读计数，增加一个 Login，用 ServerGroup.Save 更新完整入口，再编译重启，验证 InstancesChanged、新数量、新 PID 和再次附加。

## 等待和动态注册

UntilAsync 最多等待 75 秒，每 150 毫秒 Refresh 一次。条件满足即返回；若发现失败且已经退出的实例，提前结束等待。失败时输出各实例类型、PID、状态和最后日志，再抛出阶段名称，便于定位。

CheckDynamicRegistrationAsync 找到运行中的 Router apphost，从其 snapshot/config/server.yaml 读取 runDirectory，再用同一完整 YAML 启动额外 Router。等待带新进程 ProcessKey 的身份文件，要求分配到一个现有组未使用的 Router 编号。停止使用精确生命期标记，并要求原 Router 仍存活、额外进程正常退出。

## 清理与验证含义

集成主流程无论成功失败，finally 都停止本次 session 的实例并释放句柄。额外 Router 也有独立 finally，超时或失败时强制清理。轻量模式异常写标准错误并返回 1；成功返回 0。

--state-only 能验证配置快照、日志状态、PID 所属和停止文件协议，但不能证明真实 ZooKeeper 发现、进程间连接、协议导出或整组编译重启成功。完整路径会涉及外部基础服务，应按项目规范取得对应范围的测试授权后运行。
