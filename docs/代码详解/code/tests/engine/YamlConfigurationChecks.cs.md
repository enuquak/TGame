# YamlConfigurationChecks.cs 代码详解

源码：[code/tests/engine/YamlConfigurationChecks.cs](../../../../../code/tests/engine/YamlConfigurationChecks.cs)

## 职责与执行条件

`Run()` 检查统一 YAML 配置入口的展开、别名隔离、严格校验和独立快照。首先读取构建输出 `config/server.yaml`，确认日志级别为 Debug、数据库为 `tgame_test`，Mongo 与 Redis 用户名一致，Mongo 认证库与数据库一致。这一步依赖随测试输出复制的实际配置及其 YAML 别名。

## 包含文件与快照

测试在 GUID 临时目录内创建入口及 `first.yaml`、`second.yaml`。入口以默认完整配置为基础，移除原 Mongo/Redis 节，再让 include 提供这两节，保留核心字段、数量和拓扑以通过统一校验。两份包含文件都使用名为 `shared` 的锚点，但值分别为 alpha 与 beta；读取后要求各自别名保持所属文件的值，证明锚点不会跨文件串用。相对运行目录应以指定基目录解析。

`SaveSnapshot(snapshot, "logs/current")` 输出展开后的配置。删除两份源包含文件后，快照仍能独立加载；其运行目录被覆盖为新值，而原 `ServerConfiguration` 对象仍保留 `logs/original`，说明保存快照没有修改源实例。反过来再加载原入口应因包含文件已删除而抛 `FileNotFoundException`。

## 拒绝的输入

- 两个包含文件重复定义同一顶层键，或入口与包含文件重复定义键。
- `include` 不是序列、名称带父目录、带 `.yaml` 扩展名、同一名称重复出现。
- 顶层或嵌套映射中的重复键、根为列表、同一文件含多个 YAML 文档。
- 包含文件反向引用入口形成 include 循环，跨文件引用另一份文件的锚点，以及自引用的递归别名。

这些场景用 `Expect<T>` 要求对应异常，未抛错或抛错类型不符都会失败；`Check` 判断具体读取结果。

## 清理与限制

`finally` 递归删除本次创建的独立临时目录。本文件不连接外部服务，不修改仓库配置，也不逐项覆盖全部配置语义；实际解析、核心字段和拓扑校验均由 `ServerConfiguration` 完成，其他检查文件分别补充字段保持与拓扑场景。可选存储的语义校验仍在使用入口按需执行。成功信息只在所有场景执行完成后输出。
