# 引擎实体 RPC 连通性

本设计记录本次会话已确认的实现范围。

- 引擎公开 `CallEntityAsync<TReply>(string entityId, IMessage request, CancellationToken stop)` 与 `CallSinglePointAsync<TReply>(ProcessType processType, IMessage request, CancellationToken stop)`。
- 前者精确定位已登记实体；后者由 Router 在对应类型的 SinglePoint ID 集合中随机选一个，再按实体 ID 投递。一次请求只选一次，不自动重试。
- Gate 通过已连接的 Login/Base 发起请求；接收方本地命中则处理，否则交 Router。Gate 实体不作为 RPC 目标，Login/Base 对 Gate 的消息继续走既有连接。
- Router 维护实体归属与 SinglePoint 类型索引，连接断开时同步撤销；明确登记 SinglePoint ID，不从普通实体列表顺序推断。请求与回复复用已有双向流。
- 路由信封携带目标与原始调用进程。目标节点校验实体存在及 Ready，并直接调用本地 SinglePoint 的 HandleRequestAsync，实例得到目标 ID、原始调用进程及请求 ID。当前生产登记的实体是各节点 SinglePoint；动态业务实体的创建与迁移不在本轮引入。
- 回复逐跳返回；未知实体、无候选实例、未就绪、未知消息及处理失败返回本次 RPC 错误。Router 已定位的请求在目标缺失时终止，不循环转发。转发等待不能阻塞同一流上的嵌套请求。
- 保留既有无目标的连接消息处理接口。Profile 业务已经迁入自身 SinglePoint，RPC 只提供查询。
- 保留现有拓扑，不为 Gate→Base→DBMgr 增加路径或测试，不新增 Router 间连接。可调用目标以现有连接及 Router 已登记的实体为准；Router 之间的实体调用不在本轮扩展。

## 方案取舍

已选择 Router 随机选 SinglePoint、Gate 经 Login/Base 中转。调用方指定实例以及增加 Gate→Router 直连均不采用。复用现有 PeerLink 传输信封和回包，目标实例通过 SinglePoint 虚方法处理请求，不另建通信框架。

## 接入方式

宿主提供业务 SinglePoint 类型集合；引擎初始化后创建实例。业务子类重写请求入口，无需注册消息处理器。请求 ID 由引擎生成，业务入口返回 Protobuf 回复，引擎负责关联及逐跳回包。

```csharp
public override Task<IMessage> HandleRequestAsync(EntityRpcContext context, Any payload, CancellationToken stop)
{
    if (!payload.Is(StringValue.Descriptor))
    {
        return base.HandleRequestAsync(context, payload, stop);
    }

    var request = payload.Unpack<StringValue>();
    return Task.FromResult<IMessage>(new StringValue { Value = request.Value });
}
```

调用方继续使用 `node.CallEntityAsync<TReply>(entityId, request, stop)` 或 `node.CallSinglePointAsync<TReply>(processType, request, stop)`。示例类型来自 Google.Protobuf 及 Google.Protobuf.WellKnownTypes。`context` 包含 RequestId、SourceProcess、EntityId 和 PeerProcess。默认 SinglePoint 对未知协议返回 Unimplemented；`RpcDispatcher.Register<TRequest>` 仅用于无目标的连接消息。

请求携带截止时间，逐跳保留并在接收端限制最长等待；取消调用方等待不会自动撤销已执行的业务。请求只发一次，失败不换实例重发。未找到目标、无候选、未就绪、处理器缺失与处理失败通过 RPC 错误返回，不关闭健康连接。更新后须统一重建并重启服务端，旧握手没有 SinglePoint ID，不能与新节点混用。

## 验证与集成

仅运行内存连接的路由检查、现有 `--engine` 检查和相关项目编译，不启动 ZooKeeper/MongoDB/Redis 或完整集群。用户指定直接在 `codex/server-review-snapshot` 开发，同步分支规范，本轮不将已有 review 工作整体合入 main。
