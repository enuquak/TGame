# YamlConfiguration.cs 代码详解

源码：[code/server/engine/configuration/YamlConfiguration.cs](../../../../../../code/server/engine/configuration/YamlConfiguration.cs)

## 职责与生命周期

`YamlConfiguration` 是统一 YAML 配置加载器。构造时将入口及其 include 一次性读入私有 `YamlMappingNode root`，之后以配置快照方式提供读取和导出。宿主将同一个实例注册到 IoC，连接、启动数量、ZooKeeper 等配置对象从同一快照读取，运行期间不重新读文件或自动热更新。

`FileName` 为 `server.yaml`，`ConfigurationPath` 为仓库布局下的 `code/server/config/server.yaml`。`SourcePath` 保存入口绝对路径，既用于解析 include，也用于缺少配置节时的错误定位。

## Load：逐文件解析，再合并顶层

构造函数选择平台适配的路径比较器：Windows 忽略大小写，其他平台区分大小写。`loading` 集合记录当前 include 递归链；同一路径再次进入该链时抛出循环包含错误，离开时在 `finally` 中移除。

每个文件的处理顺序是：

1. 使用独立的 `YamlStream` 解析文件，要求恰好一个 YAML 文档且根节点为映射。
2. 调用 `CopyNode` 展开该文件内的别名，创建独立节点树。
3. 如果存在 `include`，要求它是序列，逐个加载同目录下的 `名称.yaml`。
4. 将被包含文件的顶层键依次加入结果，移除入口自身的 `include` 键后，再合并入口的其他顶层键。

include 项必须是不带扩展名、不含路径分隔符的单个文件名，不能为空、为 `.` 或 `..`，也不能在同一 include 列表中重复。它不是任意路径导入接口。

`Merge` 只合并顶层映射，不递归叠加同名节。所有顶层键都必须是非空标量，任意重名立即失败，包括入口和被包含文件之间的重名。因此 `connections` 不能在两个文件中各写一半，也没有“后写覆盖前写”的优先级规则。

## YAML 锚点与节点复制

每个文件独立解析，锚点和别名只在该文件内有效；跨文件别名不会被解析器共享。同名锚点可以在不同文件内分别存在，因为 `CopyNode` 在合并前已经将别名展开。

`CopyNode` 分别复制标量、序列和映射。标量保留值、样式和标签；递归使用按引用比较的 `visiting` 集合检测别名回环，并以 `MaximumNodeDepth = 64` 限制嵌套深度。它在退出节点时移除访问标记，允许同一别名在不同位置正常复用，只拒绝递归循环。

## Read：将快照转换为调用方类型

`Read<T>(section)` 不指定节时读取整个根映射；指定节时按顶层键选择节点，缺失直接抛出 `InvalidDataException`。所选节点先序列化，再交给启用 camelCase 命名和重复键检查的反序列化器转为 `T`。得到 `null` 也视为空配置错误。反序列化产生的 `YamlException` 会包装为 `InvalidDataException`，保留内部异常，并在消息中带上节名和 `SourcePath`，便于定位类型或字段问题。

各配置对象仍负责自己的业务约束，例如主动依赖必须无环、进程数量非负。加载器解决 YAML 结构与组合，不为任何角色补默认配置。

## 路径解析与输出

`ResolveRunDirectory(workingDirectory)` 读取 `runDirectory`，拒绝空白和绝对路径，然后以调用者给定工作目录解析绝对路径。校验并未要求路径位于某个子目录，合法相对路径可以含 `..`。

`SaveSnapshot(path, runDirectory)` 复制顶层映射，可在此次输出中替换运行目录，再写出完整 YAML。`SaveSection` 同样复制顶层后替换指定节，供 [ServerGroup.cs](ServerGroup.cs.md) 保存数量配置。两者都不更改已加载的 `root`，输出中没有 include 依赖，别名也已展开，适合独立运行快照。

`Serialize` 通过 `YamlStream.Save(..., assignAnchors: false)` 输出内容。保存直接写目标文件，不负责创建父目录或临时文件替换。`Load` 将 YAML 解析异常包装为带文件路径的 `InvalidDataException`；`Read` 对 YAML 反序列化异常做节级包装。文件缺失、权限等其他异常继续传播，调用者不能将解析失败视作成功加载。
