# RpcFrames.cs 代码详解

源码：[code/tests/engine/RpcFrames.cs](../../../../../code/tests/engine/RpcFrames.cs)

## 职责

`RpcFrames` 为测试创建最小的基础 RPC 帧，避免每个检查重复填写 `Frame`、`RpcRequest`、`RpcReply` 和 Protobuf `Any`。它使用引擎生成的 `TGame.Transport` 协议，不承担业务路由。

## 两个工厂方法

- `Request(message, requestId)` 新建 `Frame`，选择 `Request` 分支，将请求 ID 原样赋给 `RpcRequest.RequestId`，用 `Any.Pack(message)` 写入负载。
- `Reply(message, requestId)` 对应创建 `Reply` 分支。调用方通常把请求帧的 ID 传入，以验证待完成 RPC 的匹配和清理。

`IMessage` 允许测试使用 `StringValue` 等任意 Protobuf 消息；`Any` 保留消息类型信息，解包类型不匹配的场景由消费端测试验证。

## 边界与使用条件

这两个方法只负责组帧，不生成或校验 ID、不发送消息、不填充错误，也不管理超时和取消。需要错误应答的测试会直接构造 `RpcReply.Error`。返回对象是独立的可变消息，生命周期由测试持有者决定，没有独立资源需要释放。
