# ServerConfiguration.cs 代码详解

源码：[code/server/engine/configuration/ServerConfiguration.cs](../../../../../../code/server/engine/configuration/ServerConfiguration.cs)

## 职责与生命周期

`TGame.ServerConfiguration` 位于 Engine，统一承担 YAML 解析、配置值持有与校验、进程连接拓扑查询。它合并了原 `YamlConfiguration`、宿主 `ServerSettings` 和原连接图 `ServerConfiguration` 的职责。App 在日志创建后加载一个实例，随后将这个实例及其持有的 `Group`、`ZooKeeperOptions`、`Identifiers` 注册到根容器；运行期间不重新读文件，没有静态全局配置或热重载。

`FileName` 为 `server.yaml`，`ConfigurationPath` 为仓库布局下的 `code/server/config/server.yaml`。`SourcePath` 保存入口绝对路径。`SelectedPath()` 读取 [EngineEnvironment.Config](EngineEnvironment.cs.md) 对应的 `TGAME_SERVER_CONFIG`，变量为空时明确失败；默认入口由启动器选择。`LoadSelected()` 与 `Load(path)` 每次显式创建独立实例。

## 逐文件解析与合并

构造函数先加载入口及 include。每个文件使用独立 `YamlStream`，必须恰好包含一个 YAML 映射文档。`CopyNode` 在合并前展开文件内别名；不同文件可使用同名锚点，但不能跨文件引用锚点。按节点引用检测递归别名，并以 `MaximumNodeDepth = 64` 限制嵌套深度。

`include` 必须是文件名列表。名称不能空白、带扩展名或路径分隔符，不能是 `.`、`..` 或重复项；每项解析为同目录的 `名称.yaml`。递归链中的路径再次出现即拒绝循环包含，Windows 比较路径时忽略大小写。

`Merge` 只合并顶层键，所有键必须是非空标量。包含文件之间、入口与包含文件之间的任意重名都失败，不递归合并同名节，也没有后写覆盖前写。合并结果删除 `include`，保存在私有 `root` 中供后续读取和导出。

## 一次性绑定与配置值

`Read<ConfigurationDocument>()` 使用 camelCase 命名约定和重复键检查严格绑定全部已知字段；未知字段、类型不符等 YAML 反序列化错误包装为 `InvalidDataException`，保留源路径及内部异常。私有 `ConfigurationDocument` 只用于构造期间绑定，加载后不保存该 DTO，也不再保留公开可写的 `Processes`、`Connections` 字典。

配置直接持有 `Environment`、`RunDirectory`、`LoggingMinimumLevel`、可选 `Mongo` 和 `Redis`、`ZooKeeperOptions`、`Group`、`Identifiers`。`loggingMinimumLevel` 缺省为 `Info`；`ZooKeeper`、`DiscoveryRoot`、`MongoDatabase`、`ProfileRedis` 等便捷属性从嵌套对象计算。密码属性返回环境变量名，不读取密码。

构造期间要求环境名非空、日志级别有效、运行目录是非空相对路径。ZooKeeper 地址、发现根、会话/连接超时和 worker 根路径经过校验，参数错误包装为 `InvalidDataException`。`Identifiers` 从同一 `ZooKeeperOptions.WorkerRootPath` 生成；`Group` 校验每种类型的非负数量且至少启动一个进程。

Mongo/Redis 对象仅包含纯配置数据，属于 Engine，不引用数据库驱动。它们可缺省，基础框架启动不要求可用的业务存储。`ValidateProfileStorage()` 在独立存储入口使用时要求两份配置及 Profile 密码变量名存在，再调用各自 `Validate()`；加载成功不代表数据库已连接或存储语义已全部校验。

## 类型连接图

`ParseTargets` 通过 `ServerProcess.ParseType` 将角色文本转成枚举，拒绝 null 目标列表、未知角色、数字枚举文本和错误大小写；空列表表示没有主动依赖。`ValidateTargets` 要求每个 `ProcessType` 都有一项，目标不能重复、不能是自身或 `Gate`，并拒绝依赖环。

`ValidateAcyclic` 用深度优先遍历：`visiting` 表示当前路径，重复进入说明存在环；`visited` 表示已完成校验。进程先等待主动依赖就绪再发布地址，因此循环依赖会造成启动互等。

`targets` 保存复制后的只读字典和只读目标列表，`DialTargets` 暴露该视图。`GetDialTargets(type)` 返回主动目标；`Dials(source, target)` 查询有向边；`ArePeers(first, second)` 检查任一方向是否有边。它们服务于发现过滤与握手校验。数量门槛由 [ServerGroup.cs](ServerGroup.cs.md) 提供，实时连接和就绪状态由 [ServerNode.cs](../ServerNode.cs.md) 管理，不属于配置状态。

## 配置读取、路径与保存

`Read<T>(section)` 从已加载的 `root` 读取整个文档或一个顶层节，先序列化再严格反序列化为调用方类型；缺失或空节失败，不访问源文件。该泛型入口仍可用于工具读取原始配置数据，不建立另一份运行期配置服务。

`ResolveRunDirectory(workingDirectory)` 以调用方给出的工作目录解析 `RunDirectory`，基准不是 YAML 所在目录；合法相对路径可以含 `..`。它不创建目录。

内部 `SaveSection` 复制顶层映射后替换指定节，供数量保存及针对性检查使用。它不修改已加载的配置值和 `root`。启动器直接使用源配置，不调用配置保存方法。

写出直接覆盖目标文件，不创建父目录，也不执行临时文件替换。YAML 解析错误被包装为配置错误，文件缺失和权限等 I/O 错误继续传播。配置加载成功不意味着外部服务已就绪。
