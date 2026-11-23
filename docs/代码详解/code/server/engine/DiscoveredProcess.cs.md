# DiscoveredProcess.cs 代码详解

[源码 DiscoveredProcess.cs](../../../../../code/server/engine/DiscoveredProcess.cs)

## 一条发现记录含有哪些值

```csharp
public sealed record DiscoveredProcess(ServerProcess Process, string Address, long Generation);
```

这是 C# 的位置式 `record` 声明。圆括号列出构造参数，编译器生成同名的属性、构造函数和按值比较的逻辑。`sealed` 封住继承。`record` 在这里是引用类型，属性通过构造或初始化赋值，平常读取时保持不变。

| 属性 | 来源 | 后续用途 |
| --- | --- | --- |
| `Process` | 从 ZooKeeper 子节点名解析出的 `ServerProcess`，如类型 `Router`、编号 `1`。 | 用 `Process.Type` 匹配拓扑，从 `Process.Name` 取得连接任务的键 `Router1`。 |
| `Address` | 临时节点数据经 UTF-8 解码、验证后得到的 URI，例如 `http://127.0.0.1:5001`。 | 主动连接任务把它交给 `GrpcChannel.ForAddress`。 |
| `Generation` | ZooKeeper 节点创建事务编号 `czxid`，由 `data.Stat.getCzxid()` 返回。 | 判定同名发现目标是否被新一代临时节点替换。 |

## 谁生成记录，谁拿它拨号

`ZooKeeperProxy.WatchProcessesAsync` 读取父目录里的子节点，给成员和其数据设置观察。对子节点 `server-Router1`，它解析前缀后的身份，等到数据非空后校验服务地址，并检查临时节点的 owner。认领阶段的数据为空，先跳过。地址发布后，代理将记录放进下一批 `IReadOnlyList<DiscoveredProcess>`。成员在读取期间消失时，这一条记录也会跳过，后续观察会驱动重新读取。 [发现与发布](ZooKeeperProxy.cs.md)

`ServerNode.MaintainConnectionsAsync` 先按配置筛主动目标，再用实例名为键组成当前目标字典。随后逐个看旧的连接任务。如果字典里仍有该实例，且 `current == worker.Peer` 按值相等，就继续使用旧任务。目标消失或记录值改变时，先取消并等待旧任务，再清理其取消令牌源。有新目标时创建新的连接维护任务。记录传给 `MaintainLinkAsync` 时，实际拨号使用其名称和地址。 [发现快照驱动的任务](ServerNode.Discovery.cs.md) · [拨号与重连](ServerNode.cs.md)

## 同名同地址的重启如何看出来

假设原快照包含 `(Router1, http://127.0.0.1:5001, 100)`，随后带同样实例名的新进程重新创建临时节点，碰巧又监听相同端口。新快照若是 `(Router1, http://127.0.0.1:5001, 102)`，比较时 `Generation` 改变，发现层会重建该目标的连接任务。相反，一次刷新重新构造了记录，但三个成员值都相同，前后对象也会比较为相等。这里比较的是三项内容，`Process` 自身也是按类型和编号比较的记录。

在同一个临时节点上更新地址，`czxid` 仍是其创建事务编号。此时只要 `Address` 改变，也会导致记录比较不相等。连接任务的取消和释放发生在消费快照的发现循环中，`DiscoveredProcess` 只持有值。
