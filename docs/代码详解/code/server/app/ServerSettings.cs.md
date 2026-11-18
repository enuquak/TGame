# ServerSettings.cs 代码详解

[查看源码](../../../../../code/server/app/ServerSettings.cs)

`ServerSettings` 是宿主使用的统一 YAML 数据模型，属于 App 层；Engine 不依赖此类型。它接收合并后的顶层字段，而 include 清单已经由 `YamlConfiguration` 消费。

## 字段对应关系

`Environment`、`RunDirectory`、`Processes`、`Connections` 对应环境名、运行目录、各角色数量和主动依赖。`MinimumLogLevel` 通过 `YamlMember` 映射 `loggingMinimumLevel`，缺省为 `Info`。`Mongo`、`Redis` 是可选存储设置，`ZooKeeperSettings` 映射 `zooKeeper` 对象。

`ZooKeeper`、`DiscoveryRoot`、`ProfileMongo`、`MongoDatabase` 等计算属性把已有嵌套设置提供给现有调用方，并标注 `YamlIgnore`。它们不重新保存字段，也不成为可写入的第二套 YAML 键。密码相关属性返回的是环境变量名，不是密码内容。

## 加载与校验

`SelectedPath` 要求 `TGAME_SERVER_CONFIG` 非空，宿主自身不猜测默认环境。启动器负责选择源配置并给子进程设置快照路径。`LoadSelected` 和 `Load(path)` 是独立工具/测试的显式加载入口，不缓存全局对象。

主启动路径使用 `From(configuration)`：对已经加载的一份 YAML 绑定模型，检查环境名和日志枚举，要求运行目录为非空相对路径，再校验 ZooKeeper 地址、发现根路径及正超时。ZooKeeper 参数错误会包装成 `InvalidDataException`；未知 YAML 字段由严格绑定器拒绝。

类型关系、完整角色数量和 worker 根路径由 Program 后续创建的引擎配置对象继续校验。`From` 成功并不代表拓扑已经验证或外部服务已经连接。

## 存储按需启用

`ValidateProfileStorage` 只有独立 Profile 存储工具使用时调用：要求 Mongo、Redis 和 Profile 密码变量名存在，再分别校验连接设置。因此基础框架可以没有存储配置，但不能用缺失的存储设置打开 ProfileStore。

这些配置按运行快照使用，没有热更新逻辑。属性采用 init 初始化，但字典本身不是不可变集合；宿主据此构造只读引擎视图后不再修改它们。
