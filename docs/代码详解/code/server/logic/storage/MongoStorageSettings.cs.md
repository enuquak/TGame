# MongoStorageSettings.cs 代码详解

[查看源码](../../../../../../code/server/logic/storage/MongoStorageSettings.cs)

这个文件定义 MongoDB 独立存储的配置模型和 `MongoPasswordVariables`，属于业务存储层，不是 Engine 配置类型。

`Endpoint` 决定服务器地址，`DatabaseName` 决定读写数据库，`AuthenticationDatabase` 决定账号认证数据库，两者可以不同。`UserName` 是账号名，`ServerSelectionTimeoutSeconds` 控制驱动等待可用服务器的时长。

`PasswordVariables.Profile` 和 `PasswordVariables.Dbmgr` 保存不同用途的密码环境变量名。当前 Profile 调用方使用 Profile 项；保留 Dbmgr 声明不意味着宿主会启动 DBMgr 存储业务。

`Load(path)` 创建独立 `YamlConfiguration`，读取合并后 `mongo` 节点，并调用 `Validate`。传入统一 server.yaml 时可沿 include 读取 ops.yaml；只包含 mongo 节点的测试文档也可以使用这个入口。App 主启动路径已绑定配置，不再调用这个方法重复读文件。

`Validate` 要求地址、数据库、认证数据库和账号非空，超时为正数。它只检查配置值，不访问数据库，也不获取密码。实际密码读取和客户端创建分别由 `StorageConnections`、`MongoStorage` 完成；所需密码变量的校验由具体用途负责。
