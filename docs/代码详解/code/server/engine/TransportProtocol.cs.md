# TransportProtocol.cs 代码详解

源码：[code/server/engine/TransportProtocol.cs](../../../../../code/server/engine/TransportProtocol.cs)

## 职责

内部静态类 `TransportProtocol` 定义进程间 gRPC 建连使用的两个元数据键：`PeerHeader = "peer"` 与 `LinkHeader = "link"`。集中声明键名可让拨号端和服务端遵循同一传输约定。

## 在建连流程中的位置

[ServerNode.cs](ServerNode.cs.md) 的 `ConnectAsync` 将本进程实例名放入 `peer`，将新生成的连接 ID 放入 `link`，随 `ServerLink.Connect` 请求发送。[LinkService.cs](LinkService.cs.md) 读取这两个键，检查连接 ID 格式、允许的拓扑方向、对端临时占号和接收端就绪状态。

元数据通过检查后，双向流中的首个 `hello` 帧仍要验证进程身份。`peer` 标识对端进程，`link` 用于区分连接尝试及关联日志，二者不等同于每次 RPC 的 `request_id`。本文件只提供常量，不执行身份认证或握手逻辑。
