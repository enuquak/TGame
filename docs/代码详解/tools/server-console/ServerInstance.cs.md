# ServerInstance.cs 代码详解

[源码](../../../../tools/server-console/ServerInstance.cs)

## 一个对象如何表示一个槽位

`ServerInstance(string role, int channelId = 1)` 是 C# 主构造函数。参数直接写在类名后面，类里的属性初始化器可以用它们计算初值。 `Role` 是启动类型，`SlotName` 和 `Name` 初始都由 `ServerProcess.FormatName(role, channelId)` 生成，但在进程生命期中承担不同职责。

| 成员 | 例子和用途 |
| --- | --- |
| `Role` | `Router`。作为进程启动参数，认领结果也必须符合此类型 |
| `SlotName` | `Router1`。控制台计划中的稳定名称，用于 `Router1Server.exe`、`Router1.json` 与 `starting-Router1` |
| `Name` | 初始为 `Router1`，若服务器认领到 `Router7`，更新为 `Router7` 并切到 `Router7Server` 日志目录 |
| `process`、`Pid`、`startedUtcTicks` | 系统进程对象、可空的进程 ID、UTC 启动时间。PID 与启动时间合起来标识一次进程生命期，避免误认复用的 PID |
| `log`、`lines`、`LineCount` | 当前生命期的日志游标、最近 1000 条保留记录、该生命期累计接受条数 |
| `initializing`、`initialized`、`connected`、`failed` | 已读到的日志事件状态。 `Status` 按它们、进程存活和停止标记综合决定 |

`Alive => process is { HasExited: false }` 用属性模式判断 `process` 存在且尚未退出。 `Pid` 有值只表示记录过某次进程启动，已退出的进程也能保留 PID，用于显示和追读日志。 `ServerStatus` 是中文界面状态字符串的集中定义。 [会话如何创建槽位](ConsoleSession.cs.md)

## `Launch`、`Save` 与启动中断恢复

`Launch` 先写空标记 `starting-<SlotName>`，再用 `Reset` 释放旧句柄和清空旧状态。 `Process.Start(startInfo)` 创建子进程，读取 PID 与启动时间，建立本生命期的 `RollingLog`。 `Save` 将 `Identity(Pid, StartedUtcTicks)` 序列化成 JSON，先写 `.tmp` 再移动替换槽位文件。最后删除启动标记，界面状态进入“启动中”。

`Identity` 用 `record` 声明。恢复中把它与保存值用 `actual != saved` 比较时，比较的是 PID 和时间字段。当控制台恰好在子进程启动后、保存槽位文件前关闭时，空启动标记和独立 apphost 给下次恢复留下线索。

1. `Open(directory, logRoot, snapshot)` 重置状态并设置目录，读取槽位 JSON 的 PID 和时间，再查启动标记。有任一记录才进入 `RestoreProcess`。
2. `RestoreProcess` 用独立 apphost 的基础文件名枚举系统进程。正常恢复验证 PID、启动时间和候选的实际可执行文件全路径。存在启动标记时允许跳过原 PID 和时间的匹配，因为先前保存动作可能尚未完成。此时仍需匹配本轮快照中的完整 apphost 路径。
3. 候选在检查时退出引发特定异常，用 `catch (...) when (candidate.HasExited)` 忽略。 `finally` 释放没有被采用的候选句柄。若第二个进程也匹配，抛 `InvalidDataException`。选中的进程保留句柄与真实启动资料，记录有变化时重新 `Save`。
4. 返回 `Open` 后删除启动标记。有 PID 的槽位按照 `<PID>-<启动 ticks>-` 前缀建立日志读取器，再调用 `Refresh`。只剩旧 JSON 而找不到活进程时，仍可读取其日志并显示退出状态。

## `Refresh` 读取真实身份和日志

没有 PID 时 `Refresh` 直接返回。否则若本生命期尚未确认实际身份，就查运行目录中的 `instance-<ProcessKey>`。 `ServerRunFiles.ProcessKey` 将 PID 与启动 ticks 连成键。服务器在 ZooKeeper 认领身份并打开实例日志后，由 App 写入该文件。槽位用 `ServerProcess.Parse` 读出规范名称，类型匹配后才更新 `Name`、目录、读取器以及 `identityResolved`。这个转换只做一次。比如槽位文件仍叫 `Router1.json`，日志已从 `Router1Server` 切到 `Router7Server`。

`log?.ReadAvailable(AddLine) ?? true` 会在读取器存在时逐行调用 `AddLine`，没有读取器时把本轮视为已读完。滚动期间目录消失，或 Windows I/O 错误码为共享冲突 32、锁冲突 33，会令 `caughtUp` 为假，下一轮继续。异常过滤器通过取 `HResult` 的低 16 位拿到错误码。其他 I/O 异常由窗口的刷新入口处理。

`AddLine` 从一行 JSON 读取角色、PID、可选实例名与时间。角色或 PID 不合、实例名不合、有效时间戳早于本进程启动时间的行会提前返回。实例或时间字段缺失时按源码相应条件继续。有些 JSON 损坏、字段缺失或格式异常会命中捕获列表，原始行仍保留供诊断，但不改变状态。接受一行时增加 `LineCount`。一轮读取后裁剪 `lines` 的头部。例如累计接受 1150 行时，集合只保留最近 1000 行，界面仍可用累计值映射绝对行号。 [RollingLog 详解](RollingLog.cs.md)

## `ApplyEvent` 与界面状态优先级

| 日志事件 | 修改 |
| --- | --- |
| `LISTEN` | 将 `initializing` 置真 |
| `LINKS_READY` | 将 `connected` 置真 |
| `PROCESS_READY` | 将 `initialized` 置真 |
| `LINKS_LOST` | 将 `connected` 置假 |
| `PROCESS_FAILED` | 将 `failed` 置真，直到下次重置 |

`Refresh` 还会查找本生命期的 `stop-process-<ProcessKey>`。它按下面的顺序选取第一个匹配的状态。

1. 已读到 `PROCESS_FAILED` 时显示“异常退出”。
2. 进程已退出时，有停止标记显示“已停止”，没有该标记显示“异常退出”。
3. 进程仍活着且有停止标记时显示“停止中”。
4. 日志尚未追平时显示“初始化中”。即使这一批已经读到就绪事件，也要等日志回放完成才能确定后面是否有较新的状态。
5. 完成初始化时，`connected` 决定“就绪”或“连接不全”。其余情况按是否看到 `LISTEN` 决定“初始化中”或“启动中”。

当前服务器达成首次连接门槛后发出一次 `LINKS_READY`，发布可发现地址后写 `PROCESS_READY`。普通运行期依赖断线不会撤销引擎 `Ready`，也没有对应的 `LINKS_LOST` 日志。控制台保留旧日志解析分支，当前连接数量需通过其他观测途径了解。若启动较早阶段失败，日志文件尚未打开，控制台仍可通过进程已退出这一事实更新状态。

## `StopAsync`、`Reset` 与释放

`if (process is not { HasExited: false } child)` 在没有活进程时提前返回，同时在匹配成功时把当前对象绑定为局部变量 `child`。方法以其 PID 和保存的启动时间写本生命期停止标记，服务器轮询标记后触发自己的取消和清理。界面先显示“停止中”。

`WaitForExitAsync().WaitAsync(...)` 给等待加上 10 秒时限。超时只结束这次等待，若子进程仍活着，才记录提示并调用 `Kill`，随后再等最多 5 秒。 `Kill` 期间已退出的竞态由异常过滤器处理。强制终止后的等待超时、标记写入失败等会向上传递。完成后调用 `Refresh` 更新状态。

`Reset` 在切换组或启动前调用，释放原先句柄，清掉 PID、恢复 `Name` 为 `SlotName`、重置日志读取器、行号和事件状态。 `Dispose` 仅调用 `process?.Dispose()` 释放包装对象。服务器终止由停止流程执行，因此退出控制台后还可以根据运行资料重新附加。
