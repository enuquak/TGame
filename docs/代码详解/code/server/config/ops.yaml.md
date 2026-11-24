# ops.yaml 配置详解

源码：[code/server/config/ops.yaml](../../../../../code/server/config/ops.yaml)

这个文件集中保存第三方组件的连接地址、命名空间、用户名、密码环境变量名和超时。它是配置数据，不会自行建立连接；真正的行为要看读取这些字段的代码。

当前文件有三个顶层配置节：`mongo`、`redis`、`zooKeeperOptions`。服务器基础框架启动会使用 ZooKeeper；MongoDB 和 Redis 的配置留给独立存储调用方，不会因为写在这个文件中就自动初始化业务存储。

## 1. 它为什么由 server.yaml 加载

总入口 `server.yaml` 中写着：

```yaml
include: [ops, process]
```

这是项目的 `ServerConfiguration` 实现的加载约定，不是 YAML 自带的跨文件导入指令。加载器会找到总入口同目录的 `ops.yaml` 和 `process.yaml`，分别解析，再合并顶层配置。

`ops` 不带扩展名，由加载器补上 `.yaml`。这些路径相对于总入口文件所在目录，不是任意命令运行目录。

当前分工是：总入口保存环境、日志级别和运行目录；`ops.yaml` 保存第三方服务配置；`process.yaml` 保存进程数量和主动连接规则。各文件若出现同名顶层配置节，会被拒绝，不按 include 的先后顺序覆盖。

整个过程见 [ServerConfiguration.cs](../engine/configuration/ServerConfiguration.cs.md)。App 持有一次加载所得的对象，运行时不会不断重读 YAML，也没有把配置对象写回文件的接口。

## 2. YAML 的缩进、引号和注释怎样读

```yaml
mongo:
  endpoint: "127.0.0.1:37017"
```

`mongo:` 表示一个映射，缩进两格的 `endpoint` 是它的成员。`key: value` 表示字段和值；层级由缩进表达，不能把缩进当成纯排版。

带引号的 `127.0.0.1:37017` 是字符串。这里的冒号属于地址内容，不是新字段分隔符。以 `#` 开头的文字是注释，不作为配置值绑定。

整数值例如 `serverSelectionTimeoutSeconds: 5` 表示数值；它的单位来自字段名字和读取代码，并不是 YAML 自己知道这是几秒。

## 3. MongoDB：服务器、数据位置和认证位置

```yaml
mongo:
  endpoint: "127.0.0.1:37017"
  databaseName: &mongoDatabase tgame_test
  authenticationDatabase: *mongoDatabase
  userName: &storageUser tgame_test
  serverSelectionTimeoutSeconds: 5
  passwordVariables:
    profile: TGAME_MONGO_PASSWORD
    dbmgr: TGAME_DBMGR_MONGO_PASSWORD
```

`endpoint` 是驱动连接的地址。当前项目约定 MongoDB 通过本机 SSH 转发端口访问，所以这里写本机地址 37017。这个字符串本身不会创建或维持隧道。

`databaseName` 表示存储代码使用的数据数据库；`authenticationDatabase` 表示认证用户所在的数据库。当前二者使用相同值，但它们语义不同，不应因为值相同就合并成一个字段。

`userName` 是 MongoDB 用户名。真正创建客户端时，[MongoStorage.cs](../logic/storage/MongoStorage.cs.md) 把认证数据库、用户名以及调用方取得的密码交给驱动。

`serverSelectionTimeoutSeconds: 5` 由代码转换为 `TimeSpan.FromSeconds(...)`，限制驱动选择可用服务器的等待。它不是承诺所有存储操作都一定在 5 秒内结束，也不是 ZooKeeper 会话超时。

`passwordVariables` 下面保存的是环境变量名字：

| 用途字段 | 配置里保存的内容 |
| --- | --- |
| `profile` | 环境变量名 `TGAME_MONGO_PASSWORD` |
| `dbmgr` | 环境变量名 `TGAME_DBMGR_MONGO_PASSWORD` |

这些字符串不是密码本身。调用方通过 `Environment.GetEnvironmentVariable(key)` 按名字查找实际值。配置一个变量名不等于当前进程环境中已经存在这个变量；缺少值时存储代码会报告失败，不能凭这份 YAML 推断认证已可用。

具体取值与校验见 [StorageConnections.cs](../logic/storage/StorageConnections.cs.md)。这里保留 dbmgr 用途的字段，也不意味着基础框架启动会自动运行 DBMgr 存储业务。

## 4. & 和 * 是什么：本文件内的锚点与别名

```yaml
databaseName: &mongoDatabase tgame_test
authenticationDatabase: *mongoDatabase
```

`&mongoDatabase` 给当前值建立一个名字为 `mongoDatabase` 的锚点，`*mongoDatabase` 引用这个值。展开后的含义相当于两个字段都写 `tgame_test`，不是把 `*mongoDatabase` 当作密码或字符串原样使用。

同样，`userName: &storageUser tgame_test` 定义了一个用户名值，后面的 Redis 配置通过 `*storageUser` 复用它。

加载器在各文件内部先展开别名，再进行配置合并。因此别名不能跨文件引用：在 `ops.yaml` 定义锚点，不代表可以在 `server.yaml` 或 `process.yaml` 里直接用它。相同锚点名出现在不同文件中，也不应理解为共享同一个全局变量。

这种复用只让配置文本中的值相同，不会为数据库创建账号，也不意味着 MongoDB 和 Redis 的密码相同。

## 5. Redis：连接信息与缓存键前缀

```yaml
redis:
  endpoint: "127.0.0.1:16379"
  userName: *storageUser
  passwordVariable: TGAME_REDIS_PASSWORD
  profileCachePrefix: "tgame:test:profile:"
```

`endpoint` 同样是本机转发地址。`userName` 引用本文件前面定义的用户名，`passwordVariable` 指定读取密码的环境变量名。

`profileCachePrefix` 是缓存键的命名空间前缀。存储代码把它和具体 Profile ID 拼接，得到对应缓存键。例如，若 ID 是字符串 `123`，前缀拼接后的键可表示为 `tgame:test:profile:123`。

前缀有助于区分不同用途或环境的键，但不会自行提供访问权限、隔离数据库或实现过期策略。这些行为要看 Redis 客户端和 [ProfileStore.cs](../logic/storage/ProfileStore.cs.md) 的实际操作。

本文件没有“Redis 连接成功”的状态，加载成功只得到参数。独立存储工具在真正使用时才验证配置、取密码、建立连接和进行命令操作。

## 6. ZooKeeper：发现根、worker 根和两种超时

```yaml
zooKeeperOptions:
  endpoint: "127.0.0.1:2181"
  discoveryRoot: /tgame/test
  # 保留原有 ID worker 租约路径及 ID 分配范围。
  workerRootPath: /tgame/id-workers
  sessionTimeoutMilliseconds: 10000
  connectTimeoutMilliseconds: 10000
```

`endpoint` 是实际客户端建立连接的地址。当前配置使用本机 2181，不能因为 MongoDB、Redis 使用 SSH 转发就推断这里也是同一种连接方式。

`discoveryRoot` 下保存进程身份与已发布地址，例如 `/tgame/test/server-Router1`。进程先创建空内容的临时节点占号，监听和首次依赖连接就绪后，再把地址写入这个节点。

`workerRootPath` 下保存雪花 worker 编号的临时占用节点，例如 `/tgame/id-workers/0`。它和发现根是两个独立路径，进程实例编号也和 worker 编号分别分配。

路径值由 [ZooKeeperOptions.cs](../engine/ZooKeeperOptions.cs.md) 和 ID 选项校验，要求具体的绝对路径、不以斜杠结尾且不包含空段或 `.`、`..` 段。统一配置从这里的 `workerRootPath` 创建 `IdGeneratorOptions`，避免在多个地方维护不同的 worker 根。

两个超时都是毫秒，所以当前 `10000` 对应 10 秒：

| 字段 | 对应行为 |
| --- | --- |
| `sessionTimeoutMilliseconds` | 创建 ZooKeeper 客户端时请求的会话超时 |
| `connectTimeoutMilliseconds` | 代理本地等待连接、申请 worker 和释放 worker 时使用的操作期限 |

它们即使数值相同也不能互相替代。尤其是当前 [ZooKeeperSession.cs](../engine/ZooKeeperSession.cs.md) 会在已连接后收到断线事件时永久失效，不是必然等满 10 秒才停止发号。修改配置中的超时并不会自动改变这个状态处理策略。

## 7. 从文件到实际调用的完整路径

```text
server.yaml 声明 include
    → ServerConfiguration 读取 ops.yaml
    → 先展开文件内锚点，再合并配置节
    → 绑定配置类型，检查必要值和路径
    → App 注册同一配置及其持有的选项对象
    → ZooKeeperProxy.InitializeAsync 使用连接和会话参数
    → ID 初始化使用 workerRootPath 申请编号
```

MongoDB 和 Redis 走的是独立存储调用路径，不在当前进程基础框架的启动流程中。已有运行实例读取的是启动时加载的内存值；编辑本文件不会自动更新它，之后重新启动或补起的实例才会按其加载路径读取源配置。

启动器直接传递源配置路径和本轮运行目录，不保存或重新写出一份运行配置快照。阅读运行行为时，要把“文件中的配置”“已加载的内存对象”和“外部服务的真实状态”分开理解。
