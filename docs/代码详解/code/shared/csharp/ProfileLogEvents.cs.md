# ProfileLogEvents.cs 代码详解

源码：[code/shared/csharp/ProfileLogEvents.cs](../../../../../code/shared/csharp/ProfileLogEvents.cs)

## 职责

`TGame.ProfileLogEvents` 声明 Profile 业务日志事件名，是共享业务层的字符串契约。使用者通过常量引用事件，避免在记录和筛选处各自维护拼写。它只声明事件，不创建日志、不决定级别，也不意味着某条业务执行路径已接入。

## 事件分组

| 常量 | 业务含义 |
| --- | --- |
| `Prefix` | `PROFILE_` 前缀，可供日志策略识别 Profile 事件族 |
| `RequestTx` / `RequestRx` | 请求发送 / 接收 |
| `ReplyTx` / `ReplyRx` | 应答发送 / 接收 |
| `DeliveryFailed` / `DbError` | 投递失败 / 数据库错误 |
| `CacheHit` / `CacheUnavailable` | 缓存命中 / 缓存不可用 |
| `Missing` / `MongoRead` | 数据不存在 / MongoDB 读取 |
| `Stored` / `Removed` | 数据保存 / 移除 |

除 `Prefix` 外，值都采用完整的 `PROFILE_*` 大写标识。它们是 `const string`，没有初始化时序或线程安全状态，也没有资源清理。

## 边界

这些常量属于业务 Shared，而非通用引擎事件。日志级别、内容字段、输出目标和事件是否实际发出由调用方及日志策略决定；仅看本文件不能推断日志落盘或请求成功。
