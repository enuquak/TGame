# Router 实体归属职责迁移

**目标：** 按用户要求，将 Router 的远端实体归属与 SinglePoint 类型索引移入引擎 `RouterServerSinglePoint`，其他进程不再持有这些状态。

**实现：** 沿用 `SinglePointFor` 和 `SinglePointRegistry`，内置注册 Router 实现。`ServerNode` 保留连接、信封校验、本地分派和相邻 SinglePoint 身份；Router 实例负责归属登记、冲突检查、查询、类型选择与断线清理。注册请求早于 Router 实例创建时等待创建完成，取消仍随连接传播。

**约束：** 在 `review-1118` 完成并提交本次文件；保留宿主 `Program.cs` 已有修改。不改引擎与业务程序集依赖，不提前创建 SinglePoint，不运行完整或外部服务测试。

- [x] 在 `EntityRegistrationChecks.cs` 增加提前注册检查，先观察旧实现失败。
- [x] 新增 `RouterServerSinglePoint.cs`，修改 `SinglePointRegistry.cs`、`ServerNode.cs`、`ServerNode.Entities.cs`、`ServerNode.Routing.cs`；同步注册测试和内存网络的初始化顺序。
- [x] 更新 `docs/项目文件结构.md`；运行本地注册/路由、默认实现和 Profile 路由检查，交动态拓扑聊天统一整合提交。

**重点检查：** 提前注册不提前确认；等待期间断线不残留归属；重复实体注册拒绝且不污染索引；断线后清理并可重注册；Router 内置映射不影响其他类型的默认 SinglePoint。

**执行记录：** 提前注册检查已观察到旧实现失败；迁移后本地 `--routing`、`--engine`、`--profile` 均通过。随后并行的动态拓扑改动进入同一工作区，重叠测试和最终集成由该事项接手，不能把整份重叠文件独立提交。

**审查调整：** 归属查询明确命名为 `IsRemoteEntityOwner` / `FindRemoteEntityOwner`，本地实体仍用 `ContainsEntity`。Router 为内置实现，保留禁止业务重复注册的校验。注册调用改用连接/调用方取消控制等待，普通 RPC 保留原有超时；增加超过普通 RPC 超时才确认的回归检查。等待中断线的回归检查也已加入。

**隔离验证：** 从事项开始时的提交提取源码，在临时副本中运行真实 `ServerNode` 注册流程，复现普通 RPC 超时导致初始化失败；应用 `PeerLink` 超时参数和注册调用后，同一延迟确认检查通过。未覆盖并行动态拓扑事项的最终组合状态。

**后续调整：** 用户已批准并完成初始化后发布 ZooKeeper 地址，保留提前占用编号和 ID 租约，配套调整临时占号校验、当前就绪等待和入站握手复核；详见 [启动发布计划](2026-11-18-publish-after-ready.md)。相关短时回归通过，和动态拓扑一起提交。
