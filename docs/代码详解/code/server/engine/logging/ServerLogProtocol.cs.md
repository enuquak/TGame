# ServerLogProtocol.cs 代码详解

源码：[code/server/engine/logging/ServerLogProtocol.cs](../../../../../../code/server/engine/logging/ServerLogProtocol.cs)

## 职责与两个常量集合

`ServerLogEvents` 声明基础框架事件名，`ServerLogFields` 声明结构化日志字段名。它们是日志输出与读取工具之间的文本约定，不是消息处理器，也不保存日志状态。

## 事件的具体含义

| 常量 | 输出值 | 对应边界 |
| --- | --- | --- |
| `InitStepCompleted` | `INIT_STEP_COMPLETED` | 宿主完成一个初始化步骤 |
| `Listen` | `LISTEN` | 监听服务启动 |
| `ProcessFailed` | `PROCESS_FAILED` | 进程启动或运行失败 |
| `ProcessReady` | `PROCESS_READY` | 宿主完成启动并发布可发现地址 |
| `ZkRegister` | `ZK_REGISTER` | 已认领身份发布地址 |
| `ZkDiscover` | `ZK_DISCOVER` | 发现目标快照或发现记录问题 |
| `LinkUp` | `LINK_UP` | 对端流完成握手并进入连接集合 |
| `LinksReady` | `LINKS_READY` | 依赖连接从不满足变为满足 |
| `LinksLost` | `LINKS_LOST` | 当前依赖连接健康条件不再满足 |
| `UnmatchedReply` | `UNMATCHED_REPLY` | 回复到达时找不到待完成请求 |

`LINKS_READY` 与 `PROCESS_READY` 不相同：前者是节点连接状态变化，后者由宿主在整体启动完成后记录。`ZK_DISCOVER` 可用于调试快照，也可用于错误记录，事件名本身不决定严重级别。

## 字段协议

字段名为 `time`、`level`、`ev`、`role`、`instance`、`pid`、`peer`、`id`、`entity`、`link`、`detail`。事件字段特意使用短键 `ev`，读写方应引用常量而非猜测为 `event`。`role` 与 `instance` 分开表达类型和实例，`id` 与 `link` 分开关联请求和连接。

具体组装见 [LogEventJson.cs](LogEventJson.cs.md)，输出与过滤见 [GameLog.cs](../GameLog.cs.md)。本文件保留引擎基础事件，不在这里引入具体存储或游戏业务事件。修改文本值会影响日志消费者，应视作协议变更而非单纯重命名。
