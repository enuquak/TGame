# ServerEnvironment.cs 代码详解

源码：[code/shared/csharp/ServerEnvironment.cs](../../../../../code/shared/csharp/ServerEnvironment.cs)

## 职责与类型

本文件在 `TGame` 命名空间定义两组纯常量，供宿主与业务存储复用稳定标识。

`ServerEnvironment` 给出三个存储凭据环境变量名称：

| 常量 | 环境变量 | 用途 |
| --- | --- | --- |
| `DBMgrMongoPassword` | `TGAME_DBMGR_MONGO_PASSWORD` | DBMgr 专用 MongoDB 密码来源 |
| `MongoPassword` | `TGAME_MONGO_PASSWORD` | MongoDB 密码来源 |
| `RedisPassword` | `TGAME_REDIS_PASSWORD` | Redis 密码来源 |

配置选择键 `TGAME_SERVER_CONFIG` 已归属 Engine 的 `EngineEnvironment.Config`，不在业务 Shared 重复声明。

`ServerPeerNames.MongoDB` 的值为 `MongoDB`，用于统一外部数据库对端名称，不能与进程类型或带编号的进程实例身份混为一谈。

## 行为与边界

文件不读取、设置或校验环境变量，也不包含任何密码值。具体的默认值、覆盖优先级、占位符展开和错误处理属于配置使用方。所有成员都是编译期字符串常量，没有可变共享状态、异步操作或清理需求。增加环境变量标识时应在这里复用已有常量，而不是让调用处重复硬编码。
