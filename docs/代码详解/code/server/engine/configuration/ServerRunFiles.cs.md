# ServerRunFiles.cs 代码详解

[源码 ServerRunFiles.cs](../../../../../../code/server/engine/configuration/ServerRunFiles.cs)

## 子进程与管理端约定的文件名

`ServerRunFiles` 声明运行目录下的文件名和文件名前缀。App 读取停止文件，控制台和脚本负责给它发停止信号。程序组中每个 App 实例使用自己的 ZooKeeper 身份，同时也有一个由操作系统 PID 和启动时刻组成的启动标识。

| 成员 | 值 | 文件如何使用 |
| --- | --- | --- |
| `StopGroup` | `stop` | 某轮运行目录下出现该文件时，监控这一轮目录的 App 都能触发停止。 |
| `StopRolePrefix` | `stop-` | App 拼接的是实例名，如 `stop-Router1`。尽管成员名带 `Role`，实际监听的是这个完整实例名。 |
| `StopProcessPrefix` | `stop-process-` | 控制台和脚本拼接 `ProcessKey`，以操作系统进程的一次启动为目标。 |
| `IdentityPrefix` | `instance-` | App 拼接 `ProcessKey`，文件正文写实际获分配的名字，如 `Router1`。 |
| `DynamicDiscoveryMarker` | `dynamic-discovery` | 控制台创建空标记，打开既有会话时检查它，以判断该运行组是否支持动态身份。 |

## ProcessKey 用当前 PID 和启动时间区分一次启动

```csharp
public static string ProcessKey(int pid, long startedUtcTicks) =>
    FormattableString.Invariant($"{pid}-{startedUtcTicks}");
```

`=>` 引出方法直接返回的表达式。`$"…"` 是插值字符串，花括号中的变量会进入结果。`FormattableString.Invariant` 用固定的区域格式生成文本，保证参与方得到一致的文件名。`pid` 是操作系统进程编号，`startedUtcTicks` 是该进程的启动时间转换成 UTC 后的 .NET ticks。假设分别是 `4020` 和 `639000000000000000`，计算结果就是 `4020-639000000000000000`。以后 PID 恰好再次为 `4020` 时，其启动时间通常不同，于是旧停止文件不会命中新启动。

## 用一个 Router 实例走完读写

1. App 得到 `Router1` 的 ZooKeeper 临时占号后，先向 `instance-4020-639000000000000000.tmp` 写入 `Router1`，再移动到去掉 `.tmp` 的正式路径。控制台用子进程的 PID 和启动时间算出同一路径，读取名字、校验类型，并据此选日志子目录。
2. 控制台或脚本要停止这次启动时，创建空文件 `stop-process-4020-639000000000000000`。`WatchStopAsync` 每隔 100 毫秒检查三种停止路径，任一出现便取消 App 的运行令牌。随后由 App 的退出流程等待后台任务、停止监听并释放资源。
3. 这些文件都位于这一轮运行目录中。创建、轮询和目录管理在各调用方完成。这个类型只负责命名约定和 `ProcessKey` 的格式化。 [App 写身份与读停止信号](../../app/Program.cs.md) · [控制台读取与停止](../../../../tools/server-console/ServerInstance.cs.md) · [控制台会话标记](../../../../tools/server-console/ConsoleSession.cs.md) · [运行目录传递](EngineEnvironment.cs.md)
