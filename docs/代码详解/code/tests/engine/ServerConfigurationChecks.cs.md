# ServerConfigurationChecks.cs 代码详解

源码：[code/tests/engine/ServerConfigurationChecks.cs](../../../../../code/tests/engine/ServerConfigurationChecks.cs)

## 职责与依赖

`Run(services)` 检查类型连接拓扑、规范进程名和已加载配置对象的隔离。它读取测试输出的完整默认配置，通过 `SaveConnections` 生成临时测试文件；`SaveConnections` 使用 `YamlMappingNode` 构造连接映射，再调用内部 `SaveSection` 只替换完整配置的 `connections`。`TestServices` 仅用于构造无依赖 Router 节点，不需要初始化真实服务。

## 拓扑与独立性检查

空 `connections` 映射必须被拒绝。加载完整配置后，把磁盘文件改成空对象，已经持有的配置仍应保留 Base 到 Router、DBMgr 的主动依赖；Router 的主动目标为空。以 `Router999` 创建节点可立即完成首连，说明合法实例编号不受初始数量上限约束。再次加载默认文件必须得到独立对象。

随后修改 Router 的目标：指向 Base 会与现有 Base→Router 形成环；指向自身是自环；指向 Gate 违反禁止以 Gate 为主动目标的规则，三者均应拒绝。另将 Login→Base→Scene→Login 的三节点循环写进临时配置，再加载验证环检测。

最后构造没有任何主动连接的独立配置，再加载默认配置，要求前者仍不具有 Scene→Router，后者具有该关系，证明加载新配置不会改写已存在实例。

## 身份文本检查

`ServerProcess.TryParse` 必须拒绝没有编号的 Router、零编号、前导零、大小写不符、未知类型、纯数字、编号溢出和负数。`Router123` 应解析出编号 `123`；`ParseType` 不接受带编号的实例名或数字枚举文本，维持类型名与实例名的边界。

## 清理与限制

所有错误通过 `Check`、`Expect<T>` 转为检查失败；`finally` 删除临时文件。这里不启动连接，也不检验发现或首连数量达到过程；它验证加载时约束及对象独立性，实际就绪状态变化由连接测试负责。
