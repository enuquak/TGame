# ZooKeeperSession.cs 代码详解

源码：[code/server/engine/ZooKeeperSession.cs](../../../../../code/server/engine/ZooKeeperSession.cs)

## 职责与状态

内部 `ZooKeeperSession` 继承 ZooKeeper 的 `Watcher`，把客户端状态事件转换成进程可等待的生命周期信号。它由 [ZooKeeperProxy.cs](ZooKeeperProxy.cs.md) 持有，同一会话同时支撑进程身份和 ID worker 租约。

`connected` 表示当前已确认连接，`failed` 表示本会话已永久失效；两者使用 `volatile` 让其他线程及时观察变化。`Connected` 是初始化等待信号，`Lost` 是会话不可继续使用的信号，两者都异步运行任务续体。

## 事件转换

`process(evt)` 只把 KeeperState 传给 `StateChanged`，不做阻塞 I/O。收到 `SyncConnected` 且尚未失败时，设置连接状态并完成 `Connected`。

收到 `Expired`、`AuthFailed`，或者首次成功连接之后的 `Disconnected`，都会调用 `Invalidate`。首次连接之前的普通断开不立即宣布会话失效，可以继续等初始化超时或连接成功。

`Invalidate` 将 `failed` 设为真、`connected` 设为假，并完成两个信号。这里完成 `Connected` 不表示连接成功，而是确保初始化等待者醒来；调用方随后必须执行 `CheckActive` 区分成功与失效。

## 为什么失联后不自动恢复身份

`CheckActive` 在未连接或已失败时抛出 `InvalidOperationException`，提示重启进程重新获取身份。一旦 `failed` 为真，即使之后再收到 `SyncConnected` 也不会恢复。这样进程不会在 ZooKeeper 租约可能变化后继续使用旧实例身份或旧 worker 编号生成 ID。

此类型不自行关闭网络客户端，也不创建新会话；代理负责关闭，宿主负责响应 `SessionLost`。`SnowflakeIdGenerator` 每次发号前也调用这里的活动检查，把同一失效边界传到 ID 生成路径。
