# SinglePoint 请求入口

用户确认的职责：`CallSinglePointAsync` 由 Router 随机选取指定类型的已注册实例；目标 `ServerNode` 将请求交给本地 SinglePoint。业务逻辑属于业务 SinglePoint。Profile 提供只读查询，实体修改应通过实体 RPC 由实体自身完成。

## 方案

- 采用 SinglePoint 虚方法入口，删除外置 `ProfileMessages` 和业务消息注册汇总。
- 不保留按业务消息类型寻找 SinglePoint 的第二层分发。
- 删除 Router 的 Profile 业务校验，不增加归属查询 RPC。

## 边界

- `ServerSinglePoint.HandleRequestAsync(EntityRpcContext, Any, CancellationToken)` 默认返回 `Unimplemented`；业务子类解包并处理。
- 引擎继续负责来源校验、路由、就绪状态、截止时间和回包，不引用业务协议。
- Profile 的远程入口只接受 Fetch，保留基本参数校验和存储失败处理；旧 Publish/Withdraw 不再从 RPC 入口执行。
- 本次不创建实体修改业务或资料同步机制；现有存储辅助方法不扩展。
- 沿用 `review-1117` 分支，保留用户对宿主 `Program.cs` 的既有修改。
- 只运行编译及相关内存测试，不运行真实存储和完整多进程测试。
