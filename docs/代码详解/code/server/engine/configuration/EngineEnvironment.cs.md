# EngineEnvironment.cs 代码详解

源码：[code/server/engine/configuration/EngineEnvironment.cs](../../../../../../code/server/engine/configuration/EngineEnvironment.cs)

## 职责与使用

`EngineEnvironment` 集中声明引擎使用的环境变量名称。`Config` 的值为 `TGAME_SERVER_CONFIG`，供统一配置入口 `ServerConfiguration.SelectedPath()` 选择源文件；`LogRoot` 的值为 `TGAME_SERVER_LOG_ROOT`，供 [GameLog.cs](../GameLog.cs.md) 在打开服务器日志文件时读取。

这些常量保存环境变量的键，而非路径值。`GameLog` 读取 `LogRoot` 后，以实际值作为日志根目录；变量不存在时使用相对目录 `logs/server`。各进程实例的子目录与日志文件名仍由日志组件生成。

## 边界

本文件不读取环境变量、不检查路径、不创建目录，也不会缓存运行环境。读取时机和 I/O 错误都由使用方负责。引擎环境键放在这里，使配置和日志基础设施无需引用 App 或业务 Shared；宿主专用环境键不应无差别并入本类型。
