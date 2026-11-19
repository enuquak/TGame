# GroupChecks.cs 代码详解

源码：[code/tests/engine/GroupChecks.cs](../../../../../code/tests/engine/GroupChecks.cs)

## 检查目标

`Run()` 检查 `ServerGroup` 对启动数量的快照语义，以及类型到实例列表的命名规则。它是纯内存检查，不读配置文件，不连接服务。

先为所有 `ProcessType` 设置数量 `1`，再把 `Router` 改为 `2` 并构造组。构造之后又把原字典的 Router 数量改回 `1`，然后要求 `GetStartupMinimum(Router)` 仍为 `2`、Base 仍为 `1`。这个顺序用于证明组保存了加载时的独立快照，调用方后续修改字典不能改变运行时门槛。

## 断言内容

从 `Processes` 筛选 Router，要求名称及顺序恰好为 `Router1`、`Router2`；通过 `Single` 取出 Base，要求其名称是 `Base1` 且 `ChannelId` 为 `1`。这不仅检查多实例连续编号，也检查唯一实例不会省略编号。

失败时直接抛出异常，成功才输出 `PASS`。本文件没有资源清理需求。

## 覆盖边界

这里没有覆盖负数、零数量、未知类型、配置文件反序列化或运行时连接数量。测试中的数量用于展开计划和提供首连门槛，不能据此推断它是连接总数上限；实际就绪行为由其他节点测试检查。
