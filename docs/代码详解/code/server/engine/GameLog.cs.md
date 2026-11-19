# GameLog.cs 代码详解

源码：[code/server/engine/GameLog.cs](../../../../../code/server/engine/GameLog.cs)

## 职责与生命周期

`GameLog` 是进程共享的结构化日志服务，封装 Serilog 的文件输出和启动早期控制台输出。宿主在配置加载前创建它，认领进程身份后调用 `OpenServer` 打开实例日志，最终在其他服务清理错误都已记录后释放。对象创建并不要求先知道进程实例名。

`changes` 锁保护打开文件、修改最低级别和释放；`serverLog` 通过 `Volatile.Read/Write` 发布给写入路径。`processName` 保存打开文件时确定的实例名，`minimumLevel` 是共享的 `LoggingLevelSwitch`，使配置日志级别后立即生效。

## OpenServer 如何选目录和文件

`OpenServer` 检查对象未释放，`OpenServerCore` 拒绝重复打开。它读取当前操作系统进程的启动 UTC ticks，取得 `TGAME_SERVER_LOG_ROOT` 环境变量，缺省根目录为 `logs/server`，再创建 `实例名Server` 子目录，例如 `Router1Server`。

文件名前缀包含 PID 和启动 ticks，可区分 PID 复用及多次启动；Serilog 继续按小时和大小滚动。单文件大小限制为 10 MiB，达到限制可继续滚动，`retainedFileCountLimit: null` 表示此处不按文件数量自动清理历史日志。输出模板只写序列化后的 JSON 消息和换行，形成逐行 JSON 日志。

## 统一写入路径

`Debug`、`Info`、`Error` 都进入私有 `Write`，传入事件名、角色以及可选 peer、请求 ID、entity、link、detail。`ToSerilogLevel` 将项目的三个级别映射到 Serilog，未知枚举值明确抛出。

`Write` 先拒绝已释放对象，再按最低级别过滤；被过滤的事件连 JSON 都不构造。通过过滤后调用 [LogEventJson.cs](logging/LogEventJson.cs.md) 生成一行结构化内容：

- 文件 logger 已打开时，写入对应级别的文件事件。
- 尚未打开且 `bothPrintConsole` 为假时，写到标准输出，保证配置或初始化早期仍能看到日志。
- `bothPrintConsole` 为真时，额外写到标准错误；文件未打开时也只写标准错误，不再重复写标准输出。

`bothPrintConsole` 只在公开 `Error` 方法提供，不绕过日志级别过滤。记录中保留可选 `entity` 字段只是日志协议兼容字段，并不表示当前框架已经启动实体业务。

## 资源与错误边界

`SetMinimumLevel` 可以在文件打开前后使用。`Dispose` 幂等释放 Serilog logger，完成日志资源收尾；写入已释放日志会失败。目录创建、文件打开等 I/O 错误向宿主传播，不静默退回另一个目录。

日志依赖纯日志协议与引擎环境声明，不引用业务 Shared。使用方不应自行释放注入的 `GameLog`；它是 App 显式拥有、最后释放的共享实例。
