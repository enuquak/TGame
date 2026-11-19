# DiscoveryLinkChecks.cs 代码详解

源码：[code/tests/engine/DiscoveryLinkChecks.cs](../../../../../code/tests/engine/DiscoveryLinkChecks.cs)

## 职责与测试构成

本文件包含检查入口 `DiscoveryLinkChecks` 和模拟远端服务 `DiscoveryPeer`。测试在回环地址的随机端口启动三个真实 HTTP/2 gRPC 服务，同时使用内存通道提供服务发现快照；因此覆盖真实传输下的连接维护，但不依赖真实 ZooKeeper 发现。

`RunAsync(stop)` 构造 `Scene1`，将 Router 首连门槛设为 `2`，把 `changes.Reader.ReadAllAsync` 交给 `ServerNode.MaintainConnectionsAsync`。三个 `DiscoveredProcess` 分别带身份、实际监听地址和代次，供维护循环比较快照。

## 主要场景

1. 只公布 Router1，确认握手后仍不能完成启动；再公布 Router2，等待 `FirstLinks`。
2. 增加 Router3，要求它也会连接，说明首连门槛不是连接上限。
3. 快照缩减到 Router2，等待另两条连接真正关闭，要求首连完成后只剩一条 Router 仍可就绪。
4. 同一 Router2 的 `Generation` 改变，要求旧连接关闭并建立替代连接，覆盖同名进程重启。
5. 提供空快照并等待旧连接关闭，确认仍保持就绪；再只恢复 Router1，确认实际握手和基础 RPC 恢复，不必重新凑齐两台。
6. 以 `InvalidDataException` 结束发现通道，要求维护任务传播失败，并在结束前清理活动连接；通过对已清理实例的调用返回 `Unavailable` 验证清理，不再把 `Ready` 当成当前连接数量。

## 模拟服务的握手证据

`StartAsync` 使用 `WebApplication.CreateSlimBuilder`、`AddGrpc` 和 `MapGrpcService<DiscoveryPeer>`，只监听本机 HTTP/2。`DiscoveryPeer.Connect` 先要求调用方 Hello 为 `Scene1`，返回自己的身份，再主动发送一条带唯一请求 ID 的 `Empty` 请求。收到关联的 `Unimplemented` 应答才完成 `Connection.Handshaken`，因此检查等待的不是单纯 TCP 建连，而是已能交换基础 RPC 的链路。

每条连接都有异步继续执行的 `Handshaken`、`Closed` 完成源，连接对象通过 `Connections` 通道通知测试；`finally` 始终完成 `Closed`，让断线场景可以等待服务端退出。

## 清理与限制

外层使用关联取消源管理维护任务，`finally` 取消并等待任务，只接受预期取消或已注入的发现错误。三个 WebApplication 通过 `await using` 释放。此测试没有执行 ZooKeeper 身份占号或发布校验，模拟远端也没有使用正式 `LinkService`，所以其成功不能替代真实发现及启动发布集成检查。
