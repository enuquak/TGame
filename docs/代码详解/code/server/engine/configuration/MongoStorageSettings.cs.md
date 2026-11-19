# MongoStorageSettings.cs 代码详解

源码：[code/server/engine/configuration/MongoStorageSettings.cs](../../../../../../code/server/engine/configuration/MongoStorageSettings.cs)

`TGame.MongoStorageSettings` 是统一 YAML 中 `mongo` 节的纯配置类型，公开定义在 Engine。它保存端点、数据库、认证数据库、用户名、服务选择超时以及密码环境变量名；文件不引用 MongoDB 驱动，也不实现连接或业务存储。

`MongoPasswordVariables` 保存 `Profile` 和 `Dbmgr` 两个密码变量名，缺省为空字符串。配置不保存密码值，凭据读取由业务存储调用方负责。

`Load(path)` 创建完整的 [ServerConfiguration](ServerConfiguration.cs.md)，取得其 `Mongo` 并调用 `Validate()`。因此输入必须是有效的服务器总配置或完整快照，只有 `mongo` 节的独立片段不能作为该入口。App 已加载配置时直接使用持有的 `Mongo`，不再调用此方法重复读文件。

`Validate()` 要求端点、数据库、认证库和用户名非空白，服务选择超时为正数。它不验证密码变量名或真实认证；Profile 存储额外通过 `ServerConfiguration.ValidateProfileStorage()` 检查自己的密码变量名。缺少 Mongo 节抛 `InvalidDataException`，字段参数错误保持对应的参数异常。

实际创建客户端和连接检查仍由 [MongoStorage.cs](../../logic/storage/MongoStorage.cs.md) 完成；配置类型移入 Engine 不会让基础进程启动时连接数据库。
