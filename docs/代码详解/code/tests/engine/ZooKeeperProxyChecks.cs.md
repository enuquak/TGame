# ZooKeeperProxyChecks.cs 代码详解

源码：[code/tests/engine/ZooKeeperProxyChecks.cs](../../../../../code/tests/engine/ZooKeeperProxyChecks.cs)

## 职责与入口

`LoadOptions()` 通过 Engine 的 `ServerConfiguration.LoadSelected()` 加载当前选中入口并提取 `ZooKeeperSettings`，不依赖旧宿主配置类型。`CheckSessionAsync(log)` 检查本地初始化保护与会话状态，不需要真实 ZooKeeper；`RunAsync(log)` 则是需要显式执行的真实 ZooKeeper 集成检查。两者不能混为同一种环境检查。

## 本地初始化和会话检查

`CheckSessionAsync` 使用非法端口配置确认：未初始化时不能读取 `Process`、`SessionLost` 或查询占号；过早调用认领只拒绝当前操作，不能顺带释放实例，因此后续初始化仍进入地址解析并抛 `FormatException`。

它还创建一个只监听、不完成 ZooKeeper 握手的本地 TCP 端口，让初始化保持进行中，要求此时依旧不能暴露 `SessionLost`。清理会取消初始化、停止监听，并恢复临时关闭的 `ZooKeeper.LogToFile` 全局选项。预取消初始化、含重复斜杠的发现根和非正会话超时也分别被拒绝。

通过直接向 `ZooKeeperSession.StateChanged` 注入状态，检查首次连接尝试期间的 `Disconnected` 不算丢失已建立会话；建立后主动失效、断开、过期、鉴权失败均不可逆，即使再收到连接事件也不能恢复旧会话。首次鉴权失败需要解除初始化等待，再由活动检查明确报错。

## 真实认领、发布与发现

`RunAsync` 在配置发现根下创建 GUID 独立子根，设置 `10` 秒期限，并另建原始客户端作为其他节点的拥有者。先占用 Router1，再让代理认领 Router，要求得到 Router2，原节点地址不被改写；重复认领或以错误身份发布都必须失败。

发现流程检查空地址的 Router2 占号不出现在初始快照，发布地址后才出现；未知类型节点不进入有效集合；Scene1 动态加入、删除重建、更新地址和离开都能被观察到。同名同地址删除重建后 `Generation` 必须改变，供连接维护识别进程替换。局部 `Until` 持续拉取快照直到目标条件成立，提前结束视为失败。

之后释放代理，要求已取出的 `SessionLost` 完成；删除原 Router1，再用新代理认领，要求编号可回收为 Router1，发布后占号查询返回有效。

## 清理策略与限制

集成检查在 `finally` 释放代理，删除仅属于本次独立根的所有子节点和根，再关闭原始客户端。其清理调用没有统一附加 `stop`，不能据 `10` 秒期限推断清理阶段也有同样总时限。`WatcherStub` 不处理通知，只服务原始客户端的测试操作。

本文件只构造示例地址，不在这些端口启动服务；发现到地址不代表服务可连接。有限的动态节点场景也不覆盖 ZooKeeper 集群所有故障模式，测试代码不会改变生产启动职责。
