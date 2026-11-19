# profile.proto 代码详解

源码：[code/shared/protocols/profile.proto](../../../../../code/shared/protocols/profile.proto)

## 协议职责

此文件用 `proto3` 定义 Profile 业务数据与请求/应答。Protobuf 包名为 `tgame.profile`，生成 C# 命名空间为 `TGame.Profile`。它属于业务 Shared，与引擎连接帧和基础 RPC 协议分开；生成代码应从这里更新，不手工维护生成文件。

## 消息结构

`EntitySummary` 保存展示所需的实体摘要：字段 `1` 为 `entity_id`，`2` 为 `display_name`，`3` 为 `title`。ID 的文本是否合法、名称是否允许为空都不由这个协议文件校验。

`ProfileRequest` 的字段 `1` 是请求关联标识 `request_id`。`oneof operation` 在一条消息中选择一种操作：字段 `2` 的 `publish` 携带完整摘要，字段 `3` 的 `fetch` 携带待查询实体 ID，字段 `4` 的 `withdraw` 携带待移除实体 ID。生成 API 会维护 oneof 的互斥关系，但允许操作完全未设置，使用方需要处理这种情况。

`ProfileReply` 使用字段 `1` 回传请求 ID；字段 `2` 为 `result`；字段 `3` 为可选摘要；字段 `4` 为实体 ID。结果枚举数值依次为 `UNSPECIFIED=0`、`STORED=1`、`FOUND=2`、`MISSING=3`、`REMOVED=4`、`REJECTED=5`、`UNAVAILABLE=6`。零值是未指定状态，不应因反序列化默认值而当作操作成功。

## 使用限制与兼容性

源码注释明确：Profile RPC 只接受 `fetch`，实体数据修改由目标实体处理。协议仍保留 `publish` 和 `withdraw`，供独立存储等内部接口使用；字段存在不表示服务器当前已经注册对应的 RPC 处理器。当前进程基础框架也不会在启动时自动启用 Profile 业务。

此文件不负责路由、存储、权限、缓存、超时或资源生命周期。消息标签和枚举数值是序列化契约，已有编号不能因为改名或重排而随意复用；请求与应答的业务关联由处理层保证。
