# Program.cs 代码详解

源码：[code/tests/engine/Program.cs](../../../../../code/tests/engine/Program.cs)

## 职责与控制流

此顶层程序是引擎控制台检查的分派入口。它使用 C# 列表模式匹配参数，严格要求各模式的参数数量；成功返回退出码 `0`，未知模式或捕获到任何异常返回 `1`。异常完整写入标准错误，检查失败不会伪装成成功。

最先处理 YAML、IoC、日志三种独立模式，随后创建 `await using TestServices`。只有需要本地链路或测试 ID 的分支才初始化共享 ID 服务，避免纯配置检查无意义分配资源。

## 可选择的检查

| 参数 | 实际执行范围与环境 |
| --- | --- |
| `--yaml-configuration` | `YamlConfigurationChecks` 与 `SettingsChecks`；只读测试输出配置及临时文件。 |
| `--ioc` | `IocChecks`，包含本地会话守卫和 ID 生命周期检查，不连接真实 ZooKeeper。 |
| `--logging` | `LogLevelChecks`，使用临时日志目录与终端捕获。 |
| `--configuration` | `ServerConfigurationChecks`，验证连接拓扑及身份文本。 |
| `--profile-storage <idConfig>` | 独立 Profile 存储集成检查，需要真实 ZooKeeper、MongoDB/Redis；外层令牌期限为 60 秒。 |
| `--mongo-storage` | `MongoStorageChecks`，使用本地假服务端口验证驱动配置、取消和失败，不要求真实 MongoDB。 |
| `--id-zookeeper <idConfig>` | 先初始化本地测试 ID，再检查真实 ZooKeeper worker 分配；检查内部有自己的期限。 |
| `--registry` | 真实 ZooKeeper 占号、发布、动态发现与回收。 |
| `--zookeeper-session` | 本地会话初始化保护及 `IdGeneratorLifecycleChecks`。 |
| `--connection-readiness` | 内存首连数量门槛、运行期全部断线后保持就绪及延迟握手检查。 |
| `--node` | 仅执行 `NodeCommunicationChecks`，检查连接入口、握手和节点直接 RPC。 |
| `--rpc` | 程序集/协议边界检查及内存 `PeerLink` RPC 检查。 |
| `--startup-publication` | 本地就绪检查加真实 ZooKeeper 与本机正式 gRPC 接入；外层期限为 60 秒。 |
| `--discovery-links` | 真实本机 gRPC 与模拟发现快照，检查动态连接维护。 |
| `--dynamic-topology` | 内存连接的首次就绪、冗余链路、全部断线后保持就绪及重连。 |
| 无参数、`--engine` | 两种形式进入同一组默认检查，见下文；`--node` 已是独立专项。 |

## 默认组合与时限

默认组合依次执行连接配置、宿主设置、进程组、ID 生命周期、雪花算法、程序集边界、RPC、启动就绪和节点通信检查。它不等于列出全部可选模式：不会自动执行真实存储、真实注册、发现链路、日志或完整 YAML 专项检查。

通过参数合法性判断后，程序初始化本地 ID，再创建通常为 `5` 秒的共享取消源；启动发布模式改为 `60` 秒。令牌传给后续支持取消的异步操作，这是合作式取消，不会强行中止所有同步检查；部分检查还有内部独立时限。

## 释放与使用边界

`TestServices` 随分支返回或异常退出异步释放，内部按 ID、ZooKeeper、日志顺序清理。具体检查自行负责其链路任务、WebApplication、临时文件和测试数据。此文件是检查入口，不是服务器启动程序；执行某个模式的成功也仅代表该模式的断言通过，不能扩展为所有测试或生产环境已验证。
