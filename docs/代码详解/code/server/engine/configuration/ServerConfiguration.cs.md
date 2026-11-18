# ServerConfiguration.cs 代码详解

源码：[code/server/engine/configuration/ServerConfiguration.cs](../../../../../../code/server/engine/configuration/ServerConfiguration.cs)

## 职责与数据模型

`TGame.Server.Engine.ServerConfiguration` 将统一 YAML 的 `connections` 节转换为只读的主动连接图：键是来源 `ProcessType`，值是该类型主动拨号的目标类型列表。`FileName` 引用 `YamlConfiguration.FileName`，当前入口为 `server.yaml`。

`targets` 是这份图的权威来源。构造时复制目标数组并包为只读列表，再将字典包为 `ReadOnlyDictionary`，避免调用方修改原数组后改变运行中的拓扑。

## 加载与校验流程

`Read(configuration)` 从已加载的 [YamlConfiguration.cs](YamlConfiguration.cs.md) 读取 `connections`，适合宿主对同一配置快照分别构造多个配置对象。`Load(path)` 是独立文件调用入口，会先创建新的 YAML 快照。

字符串字典构造函数经 `ParseTargets` 转换每个角色名。它拒绝空的目标数组，并借助 `ServerProcess.ParseType` 拒绝未知角色、数字枚举文本和非规范大小写。随后内部构造函数检查：

- 所有 `ProcessType` 必须且只能各有一项，禁用主动连接也要显式配置空列表。
- 目标必须是已定义类型，不能是来源类型本身，不能指向 `Gate`。
- 同一来源下不能重复声明目标。
- 整张主动依赖图不能成环。

`ValidateAcyclic` 用深度优先遍历检查最后一项。`visiting` 表示当前递归路径，重复进入该集合说明出现环；`visited` 表示已经完整检查过的类型，避免重复遍历。拒绝依赖环是启动要求：进程先等主动依赖就绪，再发布自己的地址，环形等待无法自行解除。

## 查询与运行时关系

`GetDialTargets(type)` 返回某类型的主动目标；`Dials(source, target)` 查询有向边；`ArePeers(first, second)` 只要任一方向有边就返回真。三者分别服务于发现过滤、入站方向校验和共享双向流的邻接校验。

本类型不保存实例列表，也不限制连接数量。首次连接门槛来自 [ServerGroup.cs](ServerGroup.cs.md)，按当前连接计算就绪状态由 [ServerNode.cs](../ServerNode.cs.md) 完成。配置错误在构造时抛出；运行期没有热更新、默认回退或额外后台任务。
