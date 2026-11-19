# ServerNode.Discovery.cs 代码详解

源码：[code/server/engine/ServerNode.Discovery.cs](../../../../../code/server/engine/ServerNode.Discovery.cs)

## 职责与共享关系

这是 [ServerNode.cs](ServerNode.cs.md) 的 partial 部分，负责将连续的服务发现快照收敛为主动连接工作任务。握手、重连细节、RPC 和就绪状态仍在主文件中。它通过同一个节点的 `configuration`、`process`、`zooKeeper` 和 `log` 工作。

公开 `MaintainConnectionsAsync(stop)` 从 `ZooKeeperProxy.WatchProcessesAsync` 获取快照；内部重载接受 `IAsyncEnumerable<IReadOnlyList<DiscoveredProcess>>`，使快照消费与实际 ZooKeeper 订阅分离，也便于小范围测试注入事件序列。

## 每轮快照如何收敛

方法建立一个关联取消源 `lifetime`、首个失败信号 `failure`，以及按实例名称索引的 `workers`。每个 `ConnectionWorker` 保存发现记录、独立取消源和运行任务。

主循环同时等待下一份快照与工作任务失败。快照流意外结束会抛出 `IOException`，而不是安静结束连接维护。收到快照后先根据主动拓扑过滤，再建立目标实例字典。门槛为零的目标类型依然在这里参与发现与建连，启动数量不是过滤条件或连接上限。

随后先处理旧任务：若名称存在且整个 `DiscoveredProcess` 值相同，则继续复用；否则取消、等待任务结束、释放取消源并移除。最后为新目标创建新的关联取消源和 `RunAsync` 任务。先停旧再建新的顺序避免同名目标的新旧工作任务并行占用连接。

记录比较包含 `Generation`，因此同名同地址的进程重启也会触发任务替换；地址变化或实例从快照消失同样会触发。每次收敛完成记录 `ZK_DISCOVER` 调试日志，内容是本进程的目标名称集合。

## 失败与停止

局部 `RunAsync` 调用主文件的 `MaintainLinkAsync`。常见可恢复断线由后者内部重试；本任务取消时异常被视作正常收尾；其他异常完成 `failure`，使主循环即使正等下一次发现事件也能退出。

`finally` 先取消整个生命周期，再等待尚未完成的 `MoveNextAsync`，最后等待所有连接任务并释放其取消源。等待枚举移动完成后再异步释放枚举器，避免仍在进行中的订阅操作与释放冲突。共享 ZooKeeper 和日志的最终释放仍由宿主管理；本文件只负责自己建立的任务和取消源。

## 已读后的衔接点

下一步进入 [ServerNode.cs](ServerNode.cs.md) 的 `MaintainLinkAsync`：它调用主动入口 `ConnectAsync`，后者进入 `RunLinkAsync` 和 `RunTransportAsync`。发现层决定“要维护哪些目标”，连接层决定“一次连接怎样握手、通信和退出”。

`workers` 是主动连接维护任务，`sessions` 是主文件的同名连接占位，`links` 是可用于 RPC 的已握手连接。发现一个目标、创建 worker 都不表示握手已经成功；FirstLinks 只统计 links 中符合主动目标类型的实例。

首次达标后即使快照为空，也只是取消并清理相应 worker，Ready 仍保持为真。发现循环继续等待新快照，成员重现后重新建连；ZooKeeper 会话失效或不可恢复的 worker 异常仍会向宿主传播，不属于普通断线重试。
