# ServerLogProtocol.cs 代码详解

源码：[code/server/engine/logging/ServerLogProtocol.cs](../../../../../../code/server/engine/logging/ServerLogProtocol.cs)

这份文件没有写日志的方法。它集中规定“事件叫什么”和“JSON 属性叫什么”，让写入端、控制台读取端与测试使用同一份拼写。这里的协议是日志数据约定，不是 gRPC 的传输帧协议。

## 1. 两个静态类各自定义什么

```csharp
namespace TGame;

public static class ServerLogEvents
{
    public const string InitStepCompleted = "INIT_STEP_COMPLETED";
    public const string Listen = "LISTEN";
    public const string ProcessFailed = "PROCESS_FAILED";
    public const string ProcessReady = "PROCESS_READY";
    public const string ZkRegister = "ZK_REGISTER";
    public const string ZkDiscover = "ZK_DISCOVER";
    public const string LinkUp = "LINK_UP";
    public const string LinksReady = "LINKS_READY";
    public const string LinksLost = "LINKS_LOST";
    public const string UnmatchedReply = "UNMATCHED_REPLY";
}
```

`namespace TGame;` 让后面的类型都属于该命名空间。文件实际归属 Engine，不能只凭命名空间名字认为它来自业务 Shared。

`public static class` 是公开的静态类，不创建实例。`const string` 是编译时字符串常量，调用方写 `ServerLogEvents.Listen` 即可使用 `"LISTEN"`。C# 成员名采用 PascalCase，写入日志的数据采用大写加下划线，两者不需要字面相同。

常量集中声明的作用是减少拼错和分散约定。修改右侧文字会影响外部读取者对事件的识别；编译器不会替外部旧日志自动迁移事件名。`const` 值还可能被编译进使用方程序集，因此跨程序集修改常量时要同步考虑使用方重新构建。

## 2. 事件名与级别回答不同的问题

实际调用例子位于 `GameLog` 的使用方：

```csharp
log.Info(ServerLogEvents.InitStepCompleted, role, detail: "配置加载完成");
```

`Info` 表示重要程度；`InitStepCompleted` 表示发生了什么；`detail` 表示这一次事件的补充内容。这个常量本身不强制必须用 Info，也不会在读取它时产生任何日志。

例如 `ZooKeeperProxy` 读取无效发现数据时，可以用 `Error(ServerLogEvents.ZkDiscover, ...)`；连接维护完成快照处理后，也可用 `Debug(ServerLogEvents.ZkDiscover, ...)`。同一种主题的事件可以按情况使用不同级别。

[GameLog.cs](../GameLog.cs.md) 负责过滤和输出；[ServerLogLevel.cs](ServerLogLevel.cs.md) 规定允许的级别。

## 3. 结合调用方理解每个事件的时机

`InitStepCompleted` 在 App 多个启动阶段之后记录。它表示某个步骤完成，不等于整个进程已可服务。`Listen` 在监听启动并取得地址后记录，地址放在 `detail`；监听成功也不等于主动依赖已达到首次门槛。

`ProcessReady` 的位置更靠后：App 等待 `FirstLinks`、检查就绪、把服务地址发布到 ZooKeeper，才记录它。`ProcessFailed` 由 App 的外层失败处理记录，使用 `bothPrintConsole: true`，使失败在标准错误也可见。具体顺序见 [Program.cs](../../app/Program.cs.md)。

`ZkRegister` 在 `RegisterProcessAsync` 发布地址后记录；此前空地址临时节点的存在表示占号，不能把占号和地址可发现当作同一阶段。`ZkDiscover` 用于发现过程或无效数据诊断，见 [ZooKeeperProxy.cs](../ZooKeeperProxy.cs.md)。

`LinkUp` 在 `RunLinkAsync` 完成身份握手并把连接加入本机连接字典后记录，包含对端实例与连接 ID。它表示单条连接建立；`LinksReady` 则在首次各主动目标类型达到配置门槛时记录一次，记录点是 `firstLinks.TrySetResult()` 成功的分支。

当前 `FirstLinks` 完成后保持完成，`Ready` 也保持为真。运行期连接移除只影响连接与 RPC，不撤销这项历史完成状态。

`LinksLost` 仍是已声明常量，控制台仍能解析这个事件；但当前 `ServerNode` 的断线清理没有发出它。不能只看常量名称就推断引擎断线会记录 `LINKS_LOST`，更不能据此推断 `Ready` 会回到假。区分“格式里保留一个名字”和“当前实现有对应发出点”。

`UnmatchedReply` 在 `PeerLink` 收到找不到对应等待项的响应时记录。响应 ID 可能已超时或取消，也可能不属于当前等待表；日志用于诊断，不能仅凭它认定请求曾成功执行。见 [PeerLink.cs](../PeerLink.cs.md)。

## 4. JSON 字段名是另一组常量

```csharp
public static class ServerLogFields
{
    public const string Time = "time";
    public const string Level = "level";
    public const string Event = "ev";
    public const string Role = "role";
    public const string Instance = "instance";
    public const string Pid = "pid";
    public const string Peer = "peer";
    public const string Id = "id";
    public const string Entity = "entity";
    public const string Link = "link";
    public const string Detail = "detail";
}
```

例如 `ServerLogFields.Event` 的值是键名 `"ev"`，`ServerLogEvents.Listen` 的值是事件值 `"LISTEN"`。放到 JSON 中是：

```json
{"ev":"LISTEN"}
```

这个片段只展示一个键值对，完整记录还有其他字段。

`time` 是生成记录时的 UTC 时间，`level` 是级别的大写文本。`role` 是进程类型名，`instance` 是具体实例名，`pid` 是操作系统 PID。启动早期尚未认领实例时，`instance` 会回退为 `role`。

`peer` 关联对端实例；`id` 关联某次操作或请求；`link` 关联连接或调用点提供的标识；`detail` 放补充文本。常量只规定键，不验证值的格式，例如监听日志也把证书指纹放入 `link`。

`entity` 是保留的可选关联字段。字段存在不表示当前服务器执行实体创建、实体归属或路由；当前启动流程不做这些业务工作。

## 5. 写入端和读取端怎样共享约定

[LogEventJson.cs](LogEventJson.cs.md) 写入字典时使用字段常量：

```csharp
[ServerLogFields.Event] = ev,
[ServerLogFields.Role] = role,
```

控制台 `ServerInstance.AddLine` 则按同样的字段名读取 JSON，再把事件传给 `ApplyEvent`。它会先核对角色、PID，以及存在时的实例名和时间，避免旧进程记录污染当前进程显示。控制台文件入口见 [ServerInstance.cs](../../../../tools/server-console/ServerInstance.cs.md)。

因此一条日志既给人看，也为工具提供结构化信号。字段缺失、类型错误或事件拼错，会影响读取端；声明常量自身不完成这些校验。

本文件没有状态、异常处理或资源清理。它不负责启动顺序；启动顺序在调用方，不应反过来根据这里常量的排列顺序推导程序执行顺序。
