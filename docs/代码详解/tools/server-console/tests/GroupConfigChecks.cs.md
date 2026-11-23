# GroupConfigChecks.cs 代码详解

源码：[GroupConfigChecks.cs](../../../../../tools/server-console/tests/GroupConfigChecks.cs)

## 验证目标与隔离

本测试验证控制台统一 YAML 入口的选择、include 加载、启动数量、源配置路径和本轮运行目录。Run 保存 TGAME_SERVER_CONFIG，调用 CheckSelectedConfiguration，并在 finally 恢复环境变量；测试不把临时配置泄漏给后续检查。

CheckSelectedConfiguration 在 checkout/build/group-config-check/<GUID> 下创建最小仓库结构，自建 project.json。WriteConfiguration 写 server.yaml、ops.yaml、process.yaml 三件套：入口包含环境、日志级别与 runDirectory，ops 提供完整 Mongo 设置及必需的 ZooKeeper 地址、路径和超时，process 提供所有类型的计数与连接表，其中只有 Base→Router 是主动依赖。

## 配置选择

第一阶段清空环境变量，把所有 ProcessType 设为一个实例，要求 ConsoleSession 从默认源码路径读取到完整类型数量。第二阶段另建 selected 配置，把 Login 设为 2、Base 设为 3，再把环境变量设为相对仓库根的路径。控制台必须得到 11 个不同实例名，且两类数量正确，证明环境变量优先且相对路径基准正确。

## 可执行文件与源配置

测试只写一个内容为 apphost 的假 TGame.Server.exe，不执行它。构建 config/server.yaml 故意写成 stale 内容，随后调用真实 ConsoleSession.PrepareSnapshot。每个计划实例都应得到对应 apphost 副本，但可执行目录不得复制 config，运行目录不得生成 server.yaml。

源入口仍保留 include 和原 runDirectory；启动组不得改写源配置。

## 恢复和补起边界

重新构造 ConsoleSession 时从选中的源配置恢复 11 个槽位。通过反射调用私有 StartInfo，检查唯一命令行参数是进程类型，TGAME_SERVER_CONFIG 指向源入口，TGAME_SERVER_RUN_DIRECTORY 指向本轮运行目录。

整个测试不会实际启动假 apphost，也不验证修改配置后的运行组一致性。

## 无效输入与限制

无效数据覆盖非映射根、缺失 processes、坏 YAML、负数、非整数、未知类型、重复键以及全部类型为 0。数量相关坏数据保留有效的环境、ZooKeeper 与完整连接表，避免提前因缺少别的必需节而误过检查。全部 0 被拒绝，不表示单个类型不能为 0；单个 0 的启动语义由 Engine 配置和节点测试负责。

最后删除动态身份标记，构造控制台必须拒绝不受支持的运行记录。测试聚焦持久化边界与恢复语义。完整合成配置通过统一 ServerConfiguration 校验，但不连接外部服务。
