# ServerLogProtocol.cs 代码详解

[源码 ServerLogProtocol.cs](../../../../../../code/server/engine/logging/ServerLogProtocol.cs)

## 事件名与字段名

`ServerLogEvents` 集中定义事件的文本值，`ServerLogFields` 集中定义每行 JSON 的键。调用方写 `ServerLogEvents.ProcessReady`，序列化后出现 `"ev":"PROCESS_READY"`。控制台解析 JSON 后用 `ServerLogFields.Event` 取出 `ev`，再用同一个事件常量做匹配。两套名字分别回答“发生了什么”和“从哪个字段读取”。 [日志组装](LogEventJson.cs.md) · [控制台状态读取](../../../../tools/server-console/ServerInstance.cs.md)

两个类型都是 `public static class`，成员可以直接通过类名访问，类本身不能实例化。`public const string` 声明编译期字符串常量。修改 `ProcessReady` 这个 C# 成员名和修改 `PROCESS_READY` 这个输出值影响不同，后者会改变写入文件的文本约定，影响新旧日志的读取。

## 事件写在哪里

| 成员 | JSON 中的 `ev` | 触发点和携带信息 |
| --- | --- | --- |
| `InitStepCompleted` | `INIT_STEP_COMPLETED` | App 完成一个初始化步骤，例如配置加载，用 `detail` 说明步骤。 |
| `Listen` | `LISTEN` | App 启动监听后写入，`detail` 是监听地址。Gate 有证书时用 `link` 附带证书 SHA-256 指纹。 |
| `ProcessFailed` | `PROCESS_FAILED` | App 捕获启动或运行异常，`detail` 携带异常文本，并请求输出到标准错误。 |
| `ProcessReady` | `PROCESS_READY` | App 等首次连接门槛完成并成功发布地址后写入。 |
| `ZkRegister` | `ZK_REGISTER` | `ZooKeeperProxy.RegisterProcessAsync` 给已认领的临时节点写入地址后记录，`detail` 携带地址。 |
| `ZkDiscover` | `ZK_DISCOVER` | 节点用 Debug 记录符合拓扑的发现目标。ZooKeeper 代理也用 Error 记录无效的发现记录。 |
| `LinkUp` | `LINK_UP` | 双向流通过握手并加入可用连接集合后记录，`peer` 和 `link` 分别携带对端名和连接 ID。 |
| `LinksReady` | `LINKS_READY` | 节点首次连接门槛达标时记录，`detail` 是当时连接总数的字符串。 |
| `LinksLost` | `LINKS_LOST` | 控制台仍能读取历史日志里的该事件。当前节点运行期断线会清理连接及待处理请求，首次就绪状态保持完成。 |
| `UnmatchedReply` | `UNMATCHED_REPLY` | `PeerLink.Receive` 找不到回复对应的待处理请求时记录，例如请求超时后才收到回包。此时同时带有 `peer`、请求 `id` 和连接 `link`。 |

例如 `Login1` 成功连入 `Router1` 可能产生 `LINK_UP`，若这条连接刚好满足 `Login` 的首次门槛，则节点还会写 `LINKS_READY`。App 完成地址发布后写 `PROCESS_READY`。控制台用 `LISTEN`、`LINKS_READY`、`PROCESS_READY` 等事件维护显示状态。事件的输出级别由调用的 `Debug`、`Info` 或 `Error` 决定，`ZK_DISCOVER` 就可能出现在不同级别的行里。 [节点握手与首次连接](../ServerNode.cs.md) · [宿主发布](../../app/Program.cs.md) · [RPC 回复](../PeerLink.cs.md)

## JSON 的键由谁读取

| 成员 | 键 | 值的用途 |
| --- | --- | --- |
| `Time` | `time` | UTC 时间。控制台可据此滤掉早于当前进程启动的旧记录。 |
| `Level` | `level` | `DEBUG`、`INFO` 或 `ERROR`。 |
| `Event` | `ev` | 供显示和状态更新使用的事件名。 |
| `Role`、`Instance`、`Pid` | `role`、`instance`、`pid` | 进程类型、实例名和操作系统 PID。控制台用它们确认一行属于哪个启动实例。 |
| `Peer`、`Id`、`Link` | `peer`、`id`、`link` | 对端实例名、通常用作请求 ID 的标识、通常用作连接 ID 的标识。具体含义由事件的写入点给出。 |
| `Entity`、`Detail` | `entity`、`detail` | 可选实体关联字段、自由文本详情。当前服务器基础框架保留 `entity` 字段，事件可填 `null`。 |

例如定位一次迟到回复，可以用 `ev=UNMATCHED_REPLY` 找到类目，再用 `id` 对应请求，用 `link` 对应承载请求的连接。各键从字典写入到 JSON，再由控制台或测试读取。 [写入入口](../GameLog.cs.md) · [日志检查](../../../tests/engine/LogLevelChecks.cs.md)
