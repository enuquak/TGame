# ServerRunFiles.cs 代码详解

源码：[code/server/engine/configuration/ServerRunFiles.cs](../../../../../../code/server/engine/configuration/ServerRunFiles.cs)

## 职责与约定

`ServerRunFiles` 集中定义运行目录内的文件命名约定，让宿主与控制端共享同一协议。它只提供名称，不自行读写文件。

| 成员 | 含义 |
| --- | --- |
| `GroupConfiguration` | 引用 `ServerConfiguration.FileName`，即统一入口 `server.yaml`，不另存一份文件名常量 |
| `StopGroup` | `stop`，运行组停止标记 |
| `StopRolePrefix` | `stop-` 前缀；实际作用对象取决于调用方拼接的名称，当前 App 拼接进程实例名 |
| `StopProcessPrefix` | `stop-process-`，与操作系统进程启动身份组合的停止标记前缀 |
| `IdentityPrefix` | `instance-`，进程身份文件前缀 |
| `DynamicDiscoveryMarker` | `dynamic-discovery`，动态发现模式标记名称 |

## ProcessKey 的作用

`ProcessKey(pid, startedUtcTicks)` 用不受区域设置影响的格式生成 `PID-启动时间Ticks`。单独的 PID 可能被操作系统复用，加入启动时间后可以区分先后两次启动，避免旧停止文件命中新进程。

这和 `ServerProcess.Name` 是两个层次：`Router1` 表示游戏进程实例，`ProcessKey` 表示某一次操作系统进程启动。创建、轮询、删除这些标记及清理陈旧文件由调用方负责，本文件没有并发状态或资源生命周期。
