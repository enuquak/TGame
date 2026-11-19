# ProfileStorageChecks.cs 代码详解

源码：[code/tests/engine/ProfileStorageChecks.cs](../../../../../code/tests/engine/ProfileStorageChecks.cs)

## 职责与执行条件

`RunAsync(idConfig, log, stop)` 是独立 Profile 存储的集成检查。它读取 ID 配置，初始化真实 ZooKeeper 与 ID 服务，再用 `ServerConfiguration.LoadSelected()` 打开 `ProfileStore`。运行需要配置对应的 ZooKeeper、MongoDB/Redis 环境可用，并由入口显式选择该检查。

本检查直接调用存储对象的 `HandleAsync`，不会启动 Profile 进程或经过基础 RPC。尤其是这里构造 `Publish`、`Withdraw` 只是在验证存储接口，不代表当前进程框架公开了这些业务 RPC。

## 数据流程与断言

生成唯一 `subject` 作为测试实体 ID，依次检查：不存在时 `Missing`；发布标题 `initial` 后 `Stored`；连续两次读取都为 `Found` 且数据一致；重新发布标题 `updated` 后读取新值；撤回后 `Removed`；再次查询返回 `Missing`。连续读取与更新后读取用于覆盖缓存参与和失效后的可见结果，但没有直接断言内部缓存命中计数。

局部 `CheckAsync` 为每个请求生成独立请求 ID，调用存储并检查应答关联 ID、实体 ID、预期结果；需要内容时再检查标题与固定显示名。任意不符都会抛异常，全部完成后才输出成功信息。

## 清理策略

`finally` 使用单独的 `12` 秒取消源再次执行 `Withdraw`，即使主操作的令牌被取消，也尽量删除本次测试实体。清理结果必须是 `Removed`，否则抛出清理失败。存储对象以 `using` 释放，ID 与 ZooKeeper 以 `await using` 释放，并保持 ID 先于会话结束。

## 限制

这是少量数据的读写集成验证，不覆盖并发更新、网络分区或存储性能；唯一实体 ID 提供数据隔离，但操作确实会访问配置指向的数据库和缓存。当前服务器启动流程不自动初始化这套业务存储。
