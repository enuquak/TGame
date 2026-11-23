# transport.proto 代码详解

源码：[code/server/engine/protocols/transport.proto](../../../../../../code/server/engine/protocols/transport.proto)

## 职责与生成边界

这是引擎进程间通信的 Protobuf 源定义，采用 `proto3`，协议包名为 `tgame.transport`，生成 C# 类型放入 `TGame.Transport`。它引入 `google.protobuf.Any` 承载具体消息，使传输层无需引用业务 Shared。生成代码属于构建产物，不应通过修改生成文件改变协议。

## 消息结构

`Frame` 是双向流的统一外壳，`oneof body` 在 `hello`、`request`、`reply` 三种内容中选择一种。`hello` 只交换进程实例名，没有实体归属、路由信息或能力注册。握手之后，接收循环只接受请求和回复。

`RpcRequest` 包含 `request_id` 和 `Any payload`。请求 ID 负责同一连接上多个并发调用的匹配；`Any` 的类型 URL 让接收方和调用方可以验证具体 Protobuf 类型。请求中没有目标实体或转发地址，目标进程由所使用的连接决定。

`RpcReply` 包含对应的 `request_id`，并以 `payload` 或 `error` 表达结果。这里二者并非 Protobuf `oneof`，因此“恰好有一个”的约束由 [PeerLink.cs](../PeerLink.cs.md) 在接收时检查。`RpcError.status_code` 使用非 OK 的 gRPC 状态码，`detail` 携带可读原因。

当前字段编号如下；编号用于线上编码，不能把源文件中的排列顺序当成编号。

| 消息 | 字段编号 |
| --- | --- |
| `Frame` | `hello = 1`、`request = 2`、`reply = 3` |
| `RpcRequest` | `request_id = 1`、`payload = 2` |
| `RpcReply` | `request_id = 1`、`error = 2`、`payload = 3` |
| `RpcError` | `status_code = 1`、`detail = 2` |

当前源码已移除历史字段的 `reserved` 声明，并调整了 Frame 与 RpcReply 的编号；它与旧编号版本不是同一套线格式，阅读旧示例时以本表和当前源码为准。

## 服务与运行流程

`ServerLink.Connect` 是请求和响应均为 `stream Frame` 的双向 RPC。每对进程共享一条流，同一流可以同时收发请求与回复。[ServerNode.cs](../ServerNode.cs.md) 处理 hello、帧分发和请求队列，[LinkService.cs](../LinkService.cs.md) 实现服务入口。

当前运行框架尚未接入业务请求处理，合法请求返回 `Unimplemented`。超时和调用取消由本地 `PeerLink` 管理；协议没有取消帧，也没有把本地 deadline 发送给对端。

## 输入边界

`oneof` 允许未设置 body 的帧存在于序列化模型中，但运行时会把这种帧视为非法输入，而不是空操作。
