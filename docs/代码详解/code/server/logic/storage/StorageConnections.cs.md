# StorageConnections.cs 代码详解

[查看源码](../../../../../../code/server/logic/storage/StorageConnections.cs)

`StorageConnections` 是少量无状态的存储连接辅助函数，不是单例服务，也不保存全局客户端。

`Mongo(settings)` 从已经绑定的 `ServerConfiguration.Mongo` 取得完整配置，再使用 `MongoPasswordVariable` 从环境读取密码，调用 `MongoStorage.CreateClient`。认证数据库和选择超时直接沿用配置，不再从端点重新拼出一份硬编码设置。缺少 Mongo 节点时明确抛出配置错误。

`Password(key)` 读取指定环境变量；变量不存在时异常仅包含变量名，不打印密码。空字符串最终会在 Mongo 客户端创建校验中被拒绝。Redis 使用方也复用此方法取得其独立密码变量。

这里返回的 MongoClient 由调用者负责释放。ProfileStore 在打开失败时清理它，成功后将它纳入自身生命周期。静态工具方法不改变宿主“通过 IoC 管理进程级服务”的约定。
