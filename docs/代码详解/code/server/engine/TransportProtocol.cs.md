# TransportProtocol.cs 代码详解

[源码 TransportProtocol.cs](../../../../../code/server/engine/TransportProtocol.cs)

## gRPC 建连请求的两个头

`TransportProtocol` 规定 `ServerLink.Connect` 双向流启动时携带的两个 gRPC 元数据键。声明为 `internal static class`，日常调用在 Engine 程序集内部直接写 `TransportProtocol.PeerHeader`，无需对象。工程同时允许测试程序集访问其内部成员，建连发布测试可以用同一组常量构造请求。`public const string` 固定成员的文本值，类型的 `internal` 仍限制外部访问。

| 常量 | 传输键 | 假设由 `Login1` 主动连接 `Router1` |
| --- | --- | --- |
| `PeerHeader` | `peer` | 值为拨号方 `Login1`，由 `ServerNode.Name` 提供。 |
| `LinkHeader` | `link` | 值为这次建连时 `IdGenerator.GenId()` 生成的正整数形式字符串，例如 `512039`。 |

从接收端看，`peer` 表示远端实例，从主动端看，发出的头里放自己的名字。它与主动端局部变量 `ConnectAsync(string peer, ...)` 中的 `peer` 含义不同，后者指要拨号的目标 `Router1`。

## 头怎样进入握手

1. 主动端先确认目标名称可解析且配置允许拨号，再创建通道和连接 ID。以下集合初始化器为 `Metadata` 添加两组键值，内层 `{ 键, 值 }` 对应一次添加。
   ```csharp
   var headers = new Metadata { { TransportProtocol.PeerHeader, Name }, { TransportProtocol.LinkHeader, id } };
   ```
2. 主动端把 `headers` 传给 gRPC 的 `Connect` 调用，再向请求流写入 `hello`，其中是自己实例名 `Login1`。元数据随调用建立发送，`hello` 是调用建立后流中的首个 Protobuf 帧。
3. 入站的 `LinkService.Connect` 从 `RequestHeaders` 取两把键，缺失时通过 `?? ""` 传递空字符串，将两条流和取消令牌一起交给 `ServerNode.AcceptConnectAsync`。`ServerNode` 验证连接 ID、拓扑方向、拨号方临时占号及接收端的首次就绪状态，然后要求首帧 `Hello` 匹配预期实例名。
4. 双向握手通过后，连接 ID 用于 `LINK_UP` 等日志的 `link` 字段，连接留在节点的可用集合。后来发出的每个 RPC 通过其帧中的 `request_id` 对应回复，一条流可以承载多个请求。 [拨号、入站与握手](ServerNode.cs.md) · [gRPC 入口](LinkService.cs.md) · [流内帧定义](protocols/transport.proto.md) · [建连发布检查](../../tests/engine/StartupPublicationChecks.cs.md)
