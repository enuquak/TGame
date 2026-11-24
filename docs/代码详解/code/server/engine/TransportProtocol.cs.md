# TransportProtocol.cs 代码详解

源码：[code/server/engine/TransportProtocol.cs](../../../../../code/server/engine/TransportProtocol.cs)

这个文件规定建立 gRPC 进程连接时使用的两个元数据键。它不定义消息帧的 protobuf 字段，也不负责执行握手；理解它需要把客户端写头、服务端读头和后续验证连起来。

## 1. 常量声明怎样读

```csharp
namespace TGame.Server.Engine;

internal static class TransportProtocol
{
    public const string PeerHeader = "peer";
    public const string LinkHeader = "link";
}
```

`internal` 表示普通情况下只有当前程序集可访问这个类；测试程序集若通过 `InternalsVisibleTo` 获得授权，是另一层安排。类里成员即使写 `public`，也不会使外部普通调用方绕过类本身的可见性限制。

`static class` 表示不创建实例。`const string` 表示固定的编译时字符串。两端都引用同一个声明，避免一端写 `"peer"`，另一端误读为其他名字。

`PeerHeader` 是键名，实际值是连接发起者的实例名。`LinkHeader` 也是键名，实际值是这一次连接的 ID。常量本身既不是具体对端，也不是某条连接。

## 2. 主动连接方怎样写入元数据

[ServerNode.cs](ServerNode.cs.md) 的 `ConnectAsync` 中有：

```csharp
var id = ids.GenId();
using var channel = GrpcChannel.ForAddress(address);
var headers = new Metadata { { TransportProtocol.PeerHeader, Name }, { TransportProtocol.LinkHeader, id } };

using var call = new ServerLink.ServerLinkClient(channel).Connect(headers, cancellationToken: stop);
await call.RequestStream.WriteAsync(CreateHello(), stop);
await RunLinkAsync(peer, id, call.ResponseStream, call.RequestStream, stop);
```

`Metadata` 是 gRPC 的元数据集合。外层花括号初始化集合，内部的 `{ key, value }` 表示加入一组键值。这里大致表达 `peer = 本机实例名`、`link = 本次新生成的 ID`。

初学时容易把 `PeerHeader` 的值理解成“我要连接的目标”。实际写进去的是本机 `Name`，因为接收方需要知道“谁正在连接我”。方法局部变量 `peer` 则代表期望的对端，二者不要混淆。

`ids.GenId()` 是注入的实例服务调用，不是静态全局发号入口。`using var` 管理通道和调用对象的生命周期；退出所在作用域时释放，常量类本身不拥有这些资源。

`Connect` 建立双向流调用后，发起者还要写一帧 Hello，再进入连接运行循环。元数据不会代替流上的握手消息。

## 3. 入站服务怎样读取同一组键

[LinkService.cs](LinkService.cs.md) 读取请求头：

```csharp
var peer = context.RequestHeaders.GetValue(TransportProtocol.PeerHeader) ?? "";
var link = context.RequestHeaders.GetValue(TransportProtocol.LinkHeader) ?? "";
return node.AcceptConnectAsync(peer, link, input, output, context.CancellationToken);
```

`GetValue` 按键取出字符串；键不存在时得到 `null`。`?? ""` 是空合并，把缺失值变成空字符串，交给下一层统一验证。它不是给缺失身份补造一个有效身份。

`return` 返回 `AcceptConnectAsync` 的任务，框架会观察它的完成或失败。这个转发方法没有写 `async/await`，也仍然可以返回异步任务；没有 `await` 不代表底层操作同步结束。

请求头中的 `link` 在下一层参数名为 `id`，仍是同一个连接 ID。

## 4. 校验在哪里发生

`AcceptConnectAsync` 首先检查连接 ID 格式与进程类型连接规则，然后询问 ZooKeeper 是否存在有效的临时占号，再要求接收端自身已就绪。

这些输入来自远端，不能因为键名集中定义就直接信任。连接发起方不必预先发布可发现地址，但必须已持有有效临时占号。

之后 `RunLinkAsync` 读取第一帧，要求 `Hello` 的实例名与元数据声明的 `peer` 相同，再登记连接。缺失、非法或不一致的值，会在对应边界得到异常，不能建立正常链路。

这也解释了为什么仍要有两层数据：元数据让入口在流业务开始前取得声明身份与连接 ID，Hello 再确认流上的身份。它们不携带实体目标或业务路由信息。

## 5. 与 protobuf 字段和资源管理的区别

`"peer"`、`"link"` 是文本元数据键；[transport.proto](protocols/transport.proto.md) 中 `hello = 1`、`request = 2`、`reply = 3` 是帧字段及编号。改其中一套不会自动更新另一套。

本文件没有状态、等待、异常分支或释放操作。真实通道与连接的结束由 `ServerNode` 和 `PeerLink` 管理；断线会清理对应连接并使待处理 RPC 失败，但不会撤销已经完成的首次就绪。
