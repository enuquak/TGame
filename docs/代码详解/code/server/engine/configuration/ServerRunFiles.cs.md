# ServerRunFiles.cs 代码详解

源码：[code/server/engine/configuration/ServerRunFiles.cs](../../../../../../code/server/engine/configuration/ServerRunFiles.cs)

这份文件为服务器与控制台约定运行目录中的文件名，并提供“某一次操作系统进程生命周期”的文本键。真正创建和读取文件的是调用方；常量类自身不会停止任何进程。

## 1. 文件名和前缀的声明

```csharp
using System;

namespace TGame;

public static class ServerRunFiles
{
    public const string StopGroup = "stop";
    public const string StopRolePrefix = "stop-";
    public const string StopProcessPrefix = "stop-process-";
    public const string IdentityPrefix = "instance-";
    public const string DynamicDiscoveryMarker = "dynamic-discovery";
```

`using System` 引入后面使用的 `FormattableString`。静态类没有实例，`const` 常量不随某次启动变化。

`StopGroup` 和 `DynamicDiscoveryMarker` 是完整文件名，其余带 `Prefix` 的项只是前缀，调用方还要拼接实例名或进程键。它们都不包含目录，所以必须与本轮 `runDir` 用 `Path.Combine` 组合。

这里文件的存在通常就是信号，不依靠正文写入 `true` 等值。身份文件例外：它的正文保存最终认领的实例名。

## 2. ProcessKey 为什么同时用 PID 与启动时间

```csharp
public static string ProcessKey(int pid, long startedUtcTicks) =>
    FormattableString.Invariant($"{pid}-{startedUtcTicks}");
```

这是表达式体方法，`=>` 后面的表达式就是返回值。`int pid` 是操作系统 PID，`long startedUtcTicks` 是转换到 UTC 后的进程启动时间刻度。

`$"..."` 是插值字符串，把变量值嵌入文字。这里由 `FormattableString.Invariant` 按固定文化格式生成文本，使不同语言设置下的数字格式保持一致。

下面只是格式示意，数字不代表本轮真实进程：

```text
pid = 4020
startedUtcTicks = 639000000000000000
ProcessKey(...) = 4020-639000000000000000
```

操作系统可以复用已经退出进程的 PID。如果只用 `4020`，旧停止标记可能被后来的同 PID 进程误认。加上启动时间，把标记关联到具体这次启动。

这里的 ticks 是 `DateTime` 刻度，每个刻度 100 纳秒，不是 Unix 毫秒或随机 ID；实际获取由调用方执行，辅助方法只格式化传入值，不验证它是否来自真实进程。

## 3. 身份文件把启动时的 PID 对应到认领后的名称

App 认领完成后调用 `WriteProcessIdentity`：

```csharp
using var currentProcess = Process.GetCurrentProcess();
var identityKey = ServerRunFiles.ProcessKey(Environment.ProcessId, currentProcess.StartTime.ToUniversalTime().Ticks);
var identityPath = Path.Combine(runDir, ServerRunFiles.IdentityPrefix + identityKey);
File.WriteAllText(identityPath + ".tmp", process.Name);
File.Move(identityPath + ".tmp", identityPath, true);
return identityKey;
```

启动器启动时知道操作系统 PID 与启动时间，却还不知道 ZooKeeper 最后分配的实例编号。服务器把例如 `Login1` 的名字写入 `instance-<PID>-<UTC ticks>`，控制台随后按这个键读取，完成关联。

先写 `.tmp` 再移动到最终路径，避免正常读取者直接读取正在写入的最终文件。`true` 表示目标已存在时允许覆盖；这里不把文件系统行为扩大描述为跨平台事务保证。

`using var currentProcess` 释放的是 `Process` 包装对象及其相关句柄，不是结束当前服务器进程。身份文件的寿命也不由这个 `using` 控制。

## 4. 三种停止标记如何解释

App 的 `WatchStopAsync` 先组成路径：

```csharp
var stopGroup = Path.Combine(runDir, ServerRunFiles.StopGroup);
var stopRole = Path.Combine(runDir, ServerRunFiles.StopRolePrefix + processName);
var stopProcess = Path.Combine(runDir, ServerRunFiles.StopProcessPrefix + identityKey);
```

`stop` 作用于同一运行目录内观察它的进程。`stop-<processName>` 按实例名匹配，例如 `stop-Login1`。虽然常量名叫 `StopRolePrefix`，当前实际参数是 `process.Name`；不能把它解释成创建 `stop-Login` 就会停止整个 Login 类型。

`stop-process-<identityKey>` 则精确对应 PID 与启动时间。这是控制台 `ServerInstance.StopAsync` 使用的形式，避免实例名称重新认领或 PID 复用时误停另一轮进程。

精确性来自写入端与读取端使用同一 `ProcessKey`，不是文件前缀本身验证了身份。

## 5. 文件存在后，服务器怎样退出

监控循环继续执行：

```csharp
while (!File.Exists(stopGroup) && !File.Exists(stopRole) && !File.Exists(stopProcess))
{
    await Task.Delay(StopPollIntervalMilliseconds, lifetime.Token);
}

lifetime.Cancel();
```

`!` 表示不存在，`&&` 要求三个标记都不存在时才继续等待。任意一个出现，循环结束并请求取消生命周期。

`Task.Delay` 是异步等待，项目常量为 100 毫秒；它不是不断占用 CPU 的忙循环。传入 token 使其他退出原因也能结束等待。

`Cancel()` 发出协作式取消信号，不直接杀死进程。App 随后等待后台任务、停止监听、释放容器，最后释放日志。控制台等候超时后强制终止的逻辑属于 [ServerInstance.cs](../../../../tools/server-console/ServerInstance.cs.md)，不在这个常量类里。

## 6. dynamic-discovery 表达运行目录的模式

控制台准备运行目录时写入 `dynamic-discovery`，重新打开会话时检查该文件是否存在，用于识别支持当前动态身份发现方式的运行组。

该标记不是实时成员列表，不表示全部依赖已连接，也不代替 ZooKeeper 服务发现。它更不是配置快照：当前控制台直接传递源 YAML 路径，运行目录存放的是运行标记与相关产物。

控制台准备与恢复的上下文见 [ConsoleSession.cs](../../../../tools/server-console/ConsoleSession.cs.md)，目录来源见 [EngineEnvironment.cs](EngineEnvironment.cs.md)。

## 7. 所有权和异常的边界

此类只返回字符串，没有资源需要释放。读取文件、创建临时文件、移动文件、轮询和等待的所有权都在使用方。

文件系统权限、路径不存在或读写失败可能从调用方抛出；常量类不捕获它们，也不自动删除停止标记。新一轮运行应由运行目录和身份键区分，不能把一个固定前缀理解成完整的生命周期管理器。
