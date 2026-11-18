# RedisStorageSettings.cs 代码详解

[查看源码](../../../../../../code/server/logic/storage/RedisStorageSettings.cs)

`RedisStorageSettings` 对应统一 YAML 的 `redis` 节点，为独立 Profile 存储提供端点、用户名、密码环境变量名和缓存键前缀。

`Endpoint` 和 `UserName` 用于连接认证；`PasswordVariable` 是获取密码的环境变量名称；`ProfileCachePrefix` 用于区分缓存命名空间，之后会与实体 ID 拼成实际键。文件中不保存密码，也不创建 Redis 客户端。

`Validate` 把四个必需字符串集中检查，只要有空白值就抛 `InvalidDataException`。这项验证由 `ServerSettings.ValidateProfileStorage` 在显式打开存储时触发；基础进程框架启动不连接 Redis。

连接超时、重试次数以及缓存过期策略目前位于 `ProfileStore`，并非本配置类提供的字段。读取此文件时应区分“声明存储配置”和“启动实际存储连接”两个阶段。
