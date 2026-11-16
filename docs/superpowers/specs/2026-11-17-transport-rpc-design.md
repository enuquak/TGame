# 通用 RPC 传输帧

用户要求：去掉传输层对 Ping/Pong 的依赖，让它们作为普通 RPC 请求和响应使用。

当前 `Frame` 的 oneof 同时列出 Ping、Pong、ProfileRequest 和 ProfileReply，增加业务协议就必须修改传输协议。

采用现有 Protobuf 的 `Any` 承载消息，传输帧只包含 Hello、RpcRequest 和 RpcReply。请求和响应的信封包含 request_id，具体消息类型从 Protobuf 描述符获得，不另设手写协议编号。Ping/Pong 与 Profile 均迁移到此机制，各功能显式注册处理方法；通用收发和等待代码不引用它们。

曾考虑只把 Ping/Pong 换成 bytes、保留 Profile 专用分支；这仍会让业务类型继续扩张传输协议。另建一套动态插件或反射扫描没有当前需求，因此采用启动时显式注册。

保留连接握手、队列容量、超时、取消、实体归属校验和诊断日志。拒绝未知请求类型、缺失载荷、信封与消息请求编号不一致以及错误响应类型。请求和响应继续使用分离的读取与处理循环，以支持同一连接上的嵌套调用。

旧字段编号保留为 reserved，新字段使用新编号。需要整组重新构建并重启，不实现新旧内部协议混用。外部 WebSocket Ping/Pong 和 Profile 消息定义不变。
