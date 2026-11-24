# DiscoveredProcess.cs 代码详解

源码：[code/server/engine/DiscoveredProcess.cs](../../../../../code/server/engine/DiscoveredProcess.cs)

这份文件把一次发现得到的进程身份、地址和节点创建代次放在同一个值对象里。它很短，但记录相等比较直接决定连接维护器是否继续使用旧任务。

## 1. 一行 record 声明生成了哪些成员

```csharp
namespace TGame.Server.Engine;

public sealed record DiscoveredProcess(ServerProcess Process, string Address, long Generation);
```

`public` 允许外部程序集使用；`sealed` 禁止派生类型；这里的 `record` 默认是引用类型，不是 `record struct`。它适合表达由若干值组成的数据记录。

括号里的三个位置参数同时定义构造函数参数和公开的初始化属性：`Process`、`Address`、`Generation`。调用时需要提供三个值，之后可以通过 `peer.Process` 等方式读取。

它们分别是：

- `ServerProcess Process`：类型和实例编号组成的身份，具体声明见 [ServerGroup.cs](configuration/ServerGroup.cs.md)。
- `string Address`：可以拨号的地址。
- `long Generation`：64 位整数，来源是 ZooKeeper 节点的创建事务 ID。

编译器还为 record 生成按成员比较的相等性、相应哈希、解构等支持。这里最值得关注的是 `==`：两个不同对象，只要三个成员的值相等，就可以比较为相等。

## 2. init 与“不可变”的准确含义

位置 record 的属性通常以 `get; init;` 的方式生成。普通创建完成后，不能再像可写属性那样执行 `peer.Address = ...`；要表达新发现结果，创建新记录。

这不代表深层对象自动冻结。record 的初始化限制是浅层的，成员对象内部能否变动取决于成员类型。本例持有的 `ServerProcess` 也使用 record 身份，地址是不可变字符串，因此适合作为发现快照数据。

下面是概念例子，不是生产里直接构造发现数据的推荐入口：

```csharp
var first = new DiscoveredProcess(new ServerProcess(ProcessType.Login, 1), "http://127.0.0.1:5001", 100);
var same = new DiscoveredProcess(new ServerProcess(ProcessType.Login, 1), "http://127.0.0.1:5001", 100);
bool equal = first == same; // true
```

`first` 与 `same` 是两次创建，但 record 比较的是成员值。内部的 `ServerProcess` 同样有值相等语义，不要求是同一个引用。

## 3. 真正的构造点先验证什么

[ZooKeeperProxy.cs](ZooKeeperProxy.cs.md) 的 `ReadDiscoveredProcessAsync` 读取节点后，最终返回：

```csharp
return new DiscoveredProcess(process, address, data.Stat.getCzxid());
```

走到这里之前，代理已经检查节点名能否解析成进程身份，读取数据并跳过空地址，验证地址，还要求 `getEphemeralOwner()` 不为零，即节点确实属于一个临时会话。

因此发现列表中的记录代表“当前读取到的、已发布有效地址的临时进程节点”。仅占号、地址尚为空的节点不会生成这条记录。

`Generation` 取的是 `getCzxid()`，表示该节点创建时的 ZooKeeper 事务标识。它不是进程实例编号，不是配置版本，也不是读取发现列表的次数。这里不使用节点数据版本充当创建代次。

## 4. 为什么有相同名称和地址还不够

假设旧 `Login1` 退出，原临时节点删除；后来新进程再次认领 `Login1`，甚至使用同一地址。只比较名称和地址会把新进程当成旧进程继续维护。

节点重建后，创建事务 ID 不同，于是新的 `DiscoveredProcess.Generation` 不同，整条记录也不同。这样连接维护器能够识别“名字一样但已经换了一代”。

反过来，若同一个节点更新地址，`Generation` 可以不变，但 `Address` 变了，record 比较仍会发现差异。三个成员共同表达连接目标的身份，不应只保留其中一个。

## 5. 相等比较怎样驱动工作任务替换

[ServerNode.Discovery.cs](ServerNode.Discovery.cs.md) 按 `peer.Process.Name` 为目标建立字典，再与旧工作任务比较：

```csharp
if (targets.TryGetValue(name, out var current) && current == worker.Peer)
{
    continue;
}

worker.Stop.Cancel();
await worker.Task;
worker.Stop.Dispose();
workers.Remove(name);
```

`TryGetValue` 尝试取出同名目标，`&&` 只在确实找到后才执行记录比较。找到且整条记录相等，就保留原任务；目标消失或内容改变，则取消旧任务，等待退出，释放其取消源，再移除登记。

后续循环为仍需连接的目标创建新任务。于是“值比较”与“取消并等待旧连接维护任务”连在一起；record 不只是为了少写三个属性。

这里等待和释放的是维护任务、取消源及其所管理的连接资源，不是释放 `DiscoveredProcess` 本身。记录没有 `Dispose`，也不拥有网络通道。

## 6. 快照数据的边界

记录创建后，对端可能立刻退出；发现快照不保证后续拨号必定成功。真实连接仍要校验占号、握手身份并处理断线重试。`Generation` 也不是认证凭据。

这个类型不在构造函数里验证地址或枚举，测试可以直接构造它来模拟发现变化。生产安全边界在代理和连接入口，不能把 `public` 数据记录误读成完整校验器。

发现更新、工作任务替换和当前连接可用性都不撤销已经完成的 `FirstLinks`。首次就绪的历史状态与这份当前发现快照属于不同事实。
