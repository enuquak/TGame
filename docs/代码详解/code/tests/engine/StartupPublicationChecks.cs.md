# StartupPublicationChecks.cs 代码详解

源码：[code/tests/engine/StartupPublicationChecks.cs](../../../../../code/tests/engine/StartupPublicationChecks.cs)

## 职责与执行条件

`RunAsync(stop)` 先执行本地 `StartupReadinessChecks`，再连接真实 ZooKeeper 并启动正式 `LinkService` 的本机 gRPC 服务，验证“临时占号、监听就绪、发布地址”之间的边界。ZooKeeper 配置来自 `ServerSettings.LoadSelected()`；每次检查在原发现根下创建带 GUID 的 `startup-check-*` 子根。

## 主要流程

1. Router 通过 `ClaimProcessAsync` 取得临时身份。即使地址为空，`IsProcessClaimedAsync` 也应认可它。不存在的 Scene2、没有编号的 Scene 类型名，以及由原始客户端创建的永久 Scene3 节点都不能被当成有效活进程。
2. 使用认领身份创建 `ServerNode` 并在随机回环端口启动 HTTP/2 `LinkService`。另由原始客户端创建地址为空的 Scene1 临时节点。
3. 开启发现枚举，初始快照应为空：已占号但尚未发布地址的 Router 和 Scene 都不能被拨号。
4. 确认无主动依赖的 Router 已就绪，然后注册其监听地址。发现快照应只出现这一台 Router，未发布的 Scene 仍隐藏。
5. Scene1 带合法占号、但没有发布地址，仍可主动连接已就绪的 Router 并完成 Hello。不存在身份和永久节点身份的连接必须收到 `Unavailable`。

## 关键辅助方法

`StartAsync` 将节点和代理实例注册进测试 WebApplication，映射正式 `LinkService`。`Headers(peer)` 使用 `TransportProtocol.PeerHeader` 和 `LinkHeader`，携带对端身份与新链路 ID。`ExpectUnavailableAsync` 发起双向流并发送 Hello，只接受预期的 gRPC 错误。`WatcherStub` 仅满足原始 ZooKeeper 客户端接口，不处理事件。

## 清理与限制

gRPC 连接、频道、发现枚举和 WebApplication 均有作用域释放；正常路径显式停止应用。外层 `finally` 先释放代理，再删除本次独立根的子节点和根节点，最终关闭原始 ZooKeeper 客户端。删除范围限定为本次检查生成的根，不清理其他进程的发现数据。

此文件测试真实身份校验和发布可见性，但没有启动完整 App、多机部署或业务服务。实际是否通过取决于执行结果；源码中的 `PASS` 是全部断言成立时才会输出的文本。
