# profile.proto 代码详解

[源码](../../../../../code/shared/protocols/profile.proto)

## 从定义到 C# 对象

`syntax = "proto3"` 选择 Protobuf 语法，`package tgame.profile` 是协议的包名，`option csharp_namespace = "TGame.Profile"` 指定生成的 C# 命名空间。 `TGame.Shared.csproj` 把此文件交给 Protobuf 工具，生成消息代码到 `generated/protocols`，并设置 `GrpcServices="None"`。这里的消息可用于序列化到 Redis 等场合。文件本身没有 `service` 定义。 [Shared 项目文件](../../../../../code/shared/TGame.Shared.csproj)

字段后的整数是编码使用的标签。例如 `string title = 3;` 表示通过标签 3 识别这个字符串字段。C# 生成器把 `snake_case` 转换为 PascalCase 属性，例如 `entity_id` 对应 `EntityId`。

## `EntitySummary` 的三个展示字段

| 标签 | 字段 | C# 属性 | 用途 |
| --- | --- | --- | --- |
| 1 | `entity_id` | `EntityId` | 标识摘要对应的实体 |
| 2 | `display_name` | `DisplayName` | 显示名称 |
| 3 | `title` | `Title` | 标题 |

这些都是普通 `string` 字段，未设置时读取为空字符串。协议只定义字段类型和编码，ID 格式、展示文字校验和权限由业务调用方决定。

## `ProfileRequest` 如何选择操作

字段 1 `request_id` 是调用方填写的关联标识。 `oneof operation` 保证消息对象同一时间只选择一个操作分支。

| 标签 | 分支 | 数据 | 生成的操作判别值 |
| --- | --- | --- | --- |
| 2 | `publish` | 一个完整的 `EntitySummary` | `OperationOneofCase.Publish` |
| 3 | `fetch` | 要查找的实体 ID 字符串 | `OperationOneofCase.Fetch` |
| 4 | `withdraw` | 要移除的实体 ID 字符串 | `OperationOneofCase.Withdraw` |

```csharp
var request = new ProfileRequest { RequestId = "req-17", Fetch = "42" };
var operation = request.OperationCase;
```

此时 `operation` 为 `Fetch`，要查的是实体 `42`。若随后给同一对象的 `Withdraw` 赋值，操作会切到 `Withdraw`。新建请求若完全没给操作赋值，`OperationCase` 为 `None`。空字符串也可作为已选择分支的值，所以判断请求类型要读 `OperationCase`，仅凭字符串是否为空无法判断分支。

## `ProfileReply` 如何描述结果

| 标签 | 字段 | 如何使用 |
| --- | --- | --- |
| 1 | `request_id` | 回传请求 ID，供调用者关联到原请求 |
| 2 | `result` | 用枚举表达结果 |
| 3 | `summary` | 消息类型字段，未设置时 C# getter 返回 `null`。查到摘要时填入 |
| 4 | `entity_id` | 回传所操作的实体 ID，即使记录缺失也可填写 |

`ProfileReply.Types.Result` 是生成的 C# 嵌套枚举。数值 0 `UNSPECIFIED` 对应默认状态，1 `STORED` 表示写入，2 `FOUND` 表示查到，3 `MISSING` 表示查无记录，4 `REMOVED` 表示撤回操作完成，5 `REJECTED` 和 6 `UNAVAILABLE` 是协议保留的拒绝和不可用结果。消息解码成功后仍要读 `Result` 判断业务结果。当前 `ProfileStore` 正常路径填 `Stored`、`Found`、`Missing` 或 `Removed`，异常会按存储调用约定传播。

例如独立存储收到上述 `req-17` 请求，若找到实体 `42`，返回同一个请求 ID、`EntityId = "42"`、`Result = Found`，并带对应摘要。查无记录时仍回传请求 ID 和实体 ID，结果为 `Missing`，摘要留空。 [ProfileStore 详解](../../server/logic/storage/ProfileStore.cs.md)

## 业务边界与兼容性

协议注释规定 Profile RPC 只接收 `fetch`，实体数据修改由目标实体处理。 `publish`、`withdraw` 仍给独立存储等内部调用使用。当前服务器启动未接入 Profile RPC，这条注释说明接口设计边界。消息定义不执行鉴权、路由、缓存或数据库读写。

字段标签和枚举数值是已编码数据的契约，修改协议时要保留已有编号的含义。生成的 C# 文件由项目工具维护，定义的修改入口是此 `.proto` 文件。
