# ZooKeeperProxyChecks.cs 代码详解

源码：[code/tests/engine/ZooKeeperProxyChecks.cs](../../../../../code/tests/engine/ZooKeeperProxyChecks.cs)

## 测试入口和两种准备环境

`--zookeeper-session` 调用 `CheckSessionAsync(log)`，先用本地配置和测试 TCP 端口检查代理初始化，再直接发送会话事件。`--registry` 调用 `RunAsync(log)`，通过 `LoadOptions()` 从选中的完整服务器配置读取 ZooKeeper 选项，使用真实 ZooKeeper 检查临时节点和发现。见 [测试入口](Program.cs.md)、[TestServices.cs](TestServices.cs.md)、[ZooKeeperProxy.cs](../../server/engine/ZooKeeperProxy.cs.md)。

## CheckSessionAsync 检查初始化边界

1. **还未开始初始化。** 用 `with { Endpoint = "127.0.0.1:invalid-port" }` 从本地选项复制一份对象并替换端口。读 `Process`、读 `SessionLost`、查询占号都应抛 `InvalidOperationException`。另建一个 `earlyClaim`，先发起占号并期待同类异常，再初始化并期待解析端口时的 `FormatException`。这一步区分了拒绝过早占号与关闭代理两种行为。
2. **初始化正在等待。** 在回环地址的端口 `0` 创建 `TcpListener` 以取得系统分配的端口。该监听不会完成 ZooKeeper 握手。拿到 `InitializeAsync` 返回的任务后读 `pending.SessionLost`，断言仍会被拒绝。`finally` 取消并等待这个初始化任务，要求以 `OperationCanceledException` 结束。更内层的 `finally` 停止监听并恢复原来的 `ZooKeeper.LogToFile`，所以即使等待断言失败也会恢复。
3. **其他前置校验。** 事先取消令牌再调用初始化，应直接报告取消。`/bad//root` 因路径含空段被拒绝，`SessionTimeoutMilliseconds = 0` 因超时值非法被拒绝。

`Expect<InvalidOperationException>(() => _ = proxy.SessionLost)` 通过忽略属性返回值触发 getter 中的校验。`ExpectAsync<T>` 在 `await` 期间捕获指定异常，操作正常完成或抛出别的类型都无法通过这项断言。

## CheckSessionAsync 手工注入生命周期状态

用全新 `ZooKeeperSession` 注入 `Disconnected`，验证 `CheckActive` 失败，而 `Lost.Task` 仍未完成。再注入 `SyncConnected`，要求可用；主动调用 `Invalidate` 后重新要求不可用。

对 `Disconnected`、`Expired`、`AuthFailed` 分别重做一遍先连通再失效。每次都要满足 `Lost.Task.IsCompletedSuccessfully`，再次发送 `SyncConnected` 依然不能激活旧会话。单独的 `rejected` 从未建立连接，直接注入鉴权失败，要求 `Connected` 任务解除等待且活动检查失败。这里观察的是状态信号的转换。见 [ZooKeeperSession.cs](../../server/engine/ZooKeeperSession.cs.md)。

## RunAsync 跟踪占号、发现与回收

准备阶段从配置复制一份选项，并把发现根扩展为 `/dynamic-check-<GUID>`。`Guid.NewGuid().ToString("N")` 提供无连字符的随机标识。原始 `owner` 客户端通过空 watcher 持有外部节点，`proxy` 是接受检查的另一会话。传给 ZooKeeper 等待的主要令牌期限为 `10` 秒。

| 测试动作 | 紧接着检查的事实 |
| --- | --- |
| `owner` 逐段创建持久父节点，并预先占用带地址的 `server-Router1`。`proxy` 再认领 Router | 取得 `Router2`，原始 `Router1` 数据仍为原地址。代理不能再次认领，也不能以 `Router1` 发布 |
| 拉取第一份发现快照，然后给 `Router2` 发布地址 | 首份快照要求只有一条记录，准备阶段只有 `Router1` 带地址。发布后等到列表中出现 `Router2`，覆盖空地址占号后更新现有节点数据的路径 |
| 创建 `server-Unknown1`，再创建合法 `Scene1` | 加入未知类型后仍等待只有两条有效记录。然后要求合法 `Scene1` 出现，记录其当前 `Generation` |
| 删除 `Scene1` 并立刻用相同名称、地址重建，再改地址并删除 | 重建后等待 `Generation` 改变。改地址后等到新地址，删除后等到列表里没有该实例 |
| 离开发现枚举，保存代理的 `SessionLost` 并关闭代理；删去原有 `Router1` 后启动新代理 | 旧监督任务完成。新会话可认领 `Router1`，发布后 `IsProcessClaimedAsync` 为真 |

发现段通过 `await using` 持有异步枚举器，局部函数 `Until(Func<IReadOnlyList<DiscoveredProcess>, bool> predicate)` 在 `do / while` 中先拉取一份快照，再用断言条件决定是否继续。它拿到的是一轮轮列表，枚举提前结束即失败。场景刻意使用同名同地址替换，便于确认 `Generation` 判断来自节点身份。见 [DiscoveredProcess.cs](../../server/engine/DiscoveredProcess.cs.md)、[ServerNode.Discovery.cs](../../server/engine/ServerNode.Discovery.cs.md)。

## 清理和检查范围

集成入口在 `finally` 再次释放代理，枚举并删除本轮发现根下的所有子节点，再删除本轮根和关闭原始客户端。这组清理调用没有附加 `10` 秒令牌。地址数据用于测试发现规则，样例端口上没有运行对应的 Router 或 Scene 服务，检查范围截至 ZooKeeper 节点、地址和会话的变化。
