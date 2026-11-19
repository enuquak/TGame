# LogLevelChecks.cs 代码详解

源码：[code/tests/engine/LogLevelChecks.cs](../../../../../code/tests/engine/LogLevelChecks.cs)

## 职责与测试环境

`Run()` 检查 `GameLog` 的实例生命周期、最低级别过滤、JSON 字段、文件输出及显式终端镜像。它创建 GUID 临时根，并保存 `EngineEnvironment.LogRoot`、标准输出和标准错误的原值。测试会临时修改这些进程级对象，适合按当前控制台入口顺序执行，不是可随意并行的无副作用检查。

## 配置与输出场景

`CheckSettings(root)` 通过 `ServerConfiguration` 读取输出的完整配置，要求测试环境默认 Debug；保存独立 YAML 快照后移除 `loggingMinimumLevel`，要求缺省为 Info。逐一写入 Debug、Info、Error 的大写文本并要求正确解析；TRACE、WARNING、999 都必须以配置或 YAML 异常拒绝。

`WriteSamples` 固定写出发现 Debug、监听 Info、进程失败 Error 三条日志。主流程对每种最低级别分别检查尚未打开日志文件时的终端输出，随后检查显式错误镜像只写标准错误，不在标准输出重复。

文件模式下，为每个级别设置独立日志根并 `OpenServer(Login1)`；再次打开应失败。普通日志不能自动打印到终端。之后显式写一条包含引号与换行的错误并请求终端镜像，释放日志后读取所有 `.log` 文件，要求终端 JSON 与文件最后一条完全一致，且只出现一次。

## 行检查和生命周期

`CheckRows(lines, minimum, instance)` 计算预期级别序列，先检查行数，再逐行解析 JSON，检查级别、实例、角色 Login、当前 PID、非空事件和详情。未打开文件时实例字段使用角色，打开后使用实际 `Login1`；由此覆盖启动前后身份信息的差异。

`CheckLifecycle` 建立两份不同过滤级别的日志，释放旧实例后，写日志、改级别、开文件都必须抛 `ObjectDisposedException`；重复释放可接受，并且新实例仍按自己的 Info 级别正常工作。

## 清理与限制

外层 `finally` 恢复终端流和环境变量，再删除本次临时目录。日志对象在读取文件前已释放，确保输出已经关闭。这里使用少量样本检查格式与路由，不测并发吞吐、滚动边界或磁盘耗尽；配置和输出的通过结论必须来自实际执行，不能仅凭源码中的成功文本认定。
