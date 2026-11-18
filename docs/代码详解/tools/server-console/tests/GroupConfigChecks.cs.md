# GroupConfigChecks.cs 代码详解

源码：[GroupConfigChecks.cs](../../../../../tools/server-console/tests/GroupConfigChecks.cs)

## 验证目标与隔离

本测试验证控制台统一 YAML 入口的选择、include 展开、启动数量、完整运行快照和恢复路径。Run 保存 TGAME_SERVER_CONFIG，调用 CheckSnapshot，并在 finally 恢复环境变量；测试不把临时配置泄漏给后续检查。

CheckSnapshot 在 checkout/build/group-config-check/<GUID> 下创建最小仓库结构，自建 project.json，因此 --state-only 不需要先复制整份仓库。WriteConfiguration 写 server.yaml、ops.yaml、process.yaml 三件套：入口包含环境、日志级别与 runDirectory，ops 提供用于识别快照的 Mongo host，process 提供所有类型的计数和一条 Base→Router 连接关系。

## 配置选择

第一阶段清空环境变量，把所有 ProcessType 设为一个实例，要求 ConsoleSession 从默认源码路径读取到完整类型数量。第二阶段另建 selected 配置，把 Login 设为 2、Base 设为 3，再把环境变量设为相对仓库根的路径。控制台必须得到 11 个不同实例名，且两类数量正确，证明环境变量优先且相对路径基准正确。

## 构建与完整快照

测试只写一个内容为 apphost 的假 TGame.Server.exe，不执行它。构建 config/server.yaml 故意写成 stale 内容，随后调用真实 ConsoleSession.PrepareSnapshot，把先前加载的 YamlConfiguration 和组运行目录传进去。每个计划实例都应得到对应 apphost 副本。

运行记录中的 server.yaml 从构建快照复制。之后把默认来源改回较小数量，并删除选定入口及其两个 include 文件。逐一重新加载两个快照，要求 Login/Base 数量、environment、Mongo host、Base 连接目标和本组 runDirectory 全部保留，并且文件不再含 include。这个断言验证快照包含完整配置，而不仅是 processes 节点或原始入口文本。

## 恢复和补起边界

测试把环境变量指向不存在的文件，重新构造 ConsoleSession；由于 current 指向已有组，仍应恢复 11 个槽位。通过反射调用私有 StartInfo，检查唯一命令行参数是进程类型，TGAME_SERVER_CONFIG 指向运行目录内保存的 YAML。

删除二进制 snapshot/config/server.yaml 后，再调用 StartInfo 必须以“编译并重启”异常拒绝操作。反射将异常包装成 TargetInvocationException，测试检查其 InnerException，而不是错误地接受任意异常。整个测试不会实际启动这些假 apphost。

## 无效输入与限制

无效数据覆盖非映射根、缺失 processes、坏 YAML、负数、非整数、未知类型、重复键以及全部类型为 0。全部 0 被拒绝，不表示单个类型不能为 0；单个 0 的启动语义由 Engine 配置和节点测试负责。

最后删除动态身份标记，构造控制台必须拒绝不受支持的运行记录。测试聚焦持久化边界与恢复语义，不校验合成 Mongo 参数是否满足真实宿主设置，也不连接外部服务。
