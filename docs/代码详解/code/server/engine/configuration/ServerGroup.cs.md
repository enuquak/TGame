# ServerGroup.cs 代码详解

源码：[code/server/engine/configuration/ServerGroup.cs](../../../../../../code/server/engine/configuration/ServerGroup.cs)

## 文件中的两个类型

`ServerProcess` 是不可变进程身份记录，保存 `Type` 与 `ChannelId`；`Role` 和 `Name` 从这两个值计算，不另存同一身份的字符串副本。`ServerGroup` 则保存启动计划及每种类型的首次连接门槛，两者共用一份数量声明。

## 实例名称的生成与解析

`FormatName(role, channelId)` 要求编号至少为 `1`，使用不受区域设置影响的十进制格式拼成 `Router1`、`Router2`。唯一实例也保留编号。记录构造函数本身没有检查编号；读取 `Name` 时会经过此校验，运行节点构造时也主动读取一次名称。

`ParseType` 使用枚举解析，并再次比较 `type.ToString()` 与输入，保证只接受规范角色名。`TryParse` 从名称尾部扫描 ASCII 数字，分开角色与编号，再校验角色、整数范围和正数约束。最后用 `FormatName` 回拼检查，从而拒绝 `Router01`、`Router0`、缺少编号、错误大小写等非规范形式。失败返回 `false`；`Parse` 将同一失败转为 `InvalidDataException`。

## 数量配置与启动计划

`ServerGroup` 从统一 YAML 的 `processes` 节读取“角色名 → 数量”。字符串构造入口先通过 `ParseType` 转成枚举；枚举入口要求所有角色都有非负数量，`0` 表示该类型本轮不启动。所有数量都为零也会被拒绝。

构造函数按枚举顺序、编号从 `1` 开始展开 `Processes`，并保存只读列表。`configuredCounts` 是按角色查询数量的索引，`GetStartupMinimum` 直接读取它。这份数量既是启动计划，又是 [ServerNode.cs](../ServerNode.cs.md) 首次握手连接的门槛，并不是运行时连接数上限。真实进程身份仍由 ZooKeeper 占号决定，计划条目不等于已认领身份。

## 读写行为

`Read(configuration)` 直接返回 `configuration.Group`，不重新绑定或分配数量配置；`Load(path)` 创建完整 `ServerConfiguration` 后取得它的 `Group`。`Save(path)` 把数量写成 YAML 映射，先加载并校验目标已有的完整配置，再用 `SaveSection` 替换 `processes`，保存展开后的配置。

目标缺失、只有数量节或包含其他无效配置时直接失败，不创建不完整文件。保存已有入口时会展开其 include，不保留原来的文件拆分形式。写入不修改当前 `ServerGroup`，也不会自动创建父目录。解析错误和文件 I/O 错误向调用方传播。

相关实现：[ServerConfiguration.cs](ServerConfiguration.cs.md)、[ProcessType.cs](ProcessType.cs.md)、[ZooKeeperProxy.cs](../ZooKeeperProxy.cs.md)。
