# DiscoveredProcess.cs 代码详解

源码：[code/server/engine/DiscoveredProcess.cs](../../../../../code/server/engine/DiscoveredProcess.cs)

## 职责与字段

`DiscoveredProcess` 是服务发现快照中的不可变记录，包含三个值：`Process` 是规范的进程身份，`Address` 是已发布并通过校验的服务地址，`Generation` 是该 ZooKeeper 节点创建时的事务编号 `czxid`。

它不是正在运行的连接，也不拥有 ZooKeeper 客户端。记录由 [ZooKeeperProxy.cs](ZooKeeperProxy.cs.md) 读取临时节点后创建，再交给 [ServerNode.Discovery.cs](ServerNode.Discovery.cs.md) 调整连接工作任务。

## 为什么需要 Generation

record 的值相等比较会包含全部三个成员。当同名进程退出后重新占用相同编号，即使地址也相同，新的临时节点仍有不同的 `Generation`。发现层据此识别“同名的新一代进程”，取消旧连接任务并创建新任务，避免把它误当成没有变化的快照。

身份、地址合法性及临时节点属性由创建记录的发现层检查；这个记录自身不执行校验、I/O 或资源释放。
