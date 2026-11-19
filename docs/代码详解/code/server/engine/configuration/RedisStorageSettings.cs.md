# RedisStorageSettings.cs 代码详解

源码：[code/server/engine/configuration/RedisStorageSettings.cs](../../../../../../code/server/engine/configuration/RedisStorageSettings.cs)

`TGame.RedisStorageSettings` 是统一 YAML 中 `redis` 节的公开纯配置类型，位于 Engine。它保存端点、用户名、密码环境变量名和 Profile 缓存键前缀，使用 init 属性绑定；所有字符串缺省为空。

`Validate()` 要求四项均非空白，否则抛 `InvalidDataException`。`ServerConfiguration` 绑定并持有这个可选对象，真正打开 Profile 存储时由 `ValidateProfileStorage()` 触发语义校验；基础框架不要求 Redis 配置，也不会因此建立连接。

本文件不引用 Redis 驱动、不读取密码、不管理缓存。[ProfileStore.cs](../../logic/storage/ProfileStore.cs.md) 位于 logic，负责实际连接和业务存储行为。
