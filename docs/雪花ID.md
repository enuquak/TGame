# 统一 ID 生成

引擎通过 `IdGenerator` 实例的 `GenId()` 返回使用 `CultureInfo.InvariantCulture` 格式化的十进制 `string`。App 将生成器注册为 IoC 单例，同一进程的节点和独立存储工具通过构造函数或参数接收同一个实例，不再调用静态服务入口。

```csharp
using TGame.Server.Engine;

// ids 为构造函数或方法参数传入的 IdGenerator。
string id = ids.GenId();
```

宿主先创建日志并加载配置，再构建一个 IoC 根容器。`ZooKeeperProxy` 和 `IdGenerator` 分别注册为单例；构造对象不执行异步初始化。宿主显式连接 ZooKeeper、占用进程实例编号，然后初始化 ID 生成器，最后启动节点监听与依赖连接。代理持有进程唯一的连接和会话，生成器通过注入的代理申请 worker 编号；进程实例编号与 worker 编号是两种独立标识。下面是容器构建后的初始化顺序示意：

```csharp
var zooKeeper = app.Services.GetRequiredService<ZooKeeperProxy>();
await zooKeeper.InitializeAsync(stop);
await zooKeeper.ClaimProcessAsync(processType, stop);

var ids = app.Services.GetRequiredService<IdGenerator>();
await ids.InitializeAsync(stop);
```

服务解析集中在 App 组装入口；使用方通过构造函数接收依赖，不自行查询容器。App 在后台工作退出、监听停止后异步释放根容器，先释放生成器的 worker 租约，再关闭 ZooKeeper 代理。宿主通过 `zooKeeper.SessionLost` 监督共享会话；`ids.LeaseLost` 指向同一失效通知。现有协议和日志的字符串字段直接使用 `ids.GenId()` 返回值。提前创建并注册的日志实例由 App 最后显式释放。

未初始化、初始化尚未完成或已释放时调用会明确报错，同一个对象的重复初始化也会报错。生命周期由宿主按顺序管理，构造和解析对象不会绕过初始化校验。初始化失败或取消不会留下可用的半初始化生成器，已经创建的生成器会被清理。单例共享范围属于容器，不再使用静态全局实例；不同测试容器可独立创建和释放服务。正常服务器仍在租约丢失后退出，由重启重新申请编号。

底层 `SnowflakeIdGenerator` 保留算法、时钟回拨和租约有效性检查，不直接创建 ZooKeeper 客户端或读写节点。节点创建、读取、监听、worker 申请与释放均由 `ZooKeeperProxy` 完成；代理关闭后旧生成器也立即失效。外部不再使用 `IIdGenerator` 或访问节点上的生成器属性。

`IdText.IsValid()` 校验正整数的规范文本，拒绝负号、空白、前导零和超出 `long` 范围的值。

## 算法与编号分配

- 41 位毫秒时间戳、10 位 WorkerId、12 位序号，最高符号位为 0；起始时间固定为 `2026-01-01 00:00:00 UTC`，已有 ID 后不能随意修改。
- ZooKeeper 用临时节点独占 `0～1023` 中的一个编号。共用 ID 空间的进程必须使用相同集群和根路径。
- 每毫秒最多生成 4096 个 ID，序号用尽时等待下一毫秒；并发调用在本地串行分配序号。
- 正常生成不访问 ZooKeeper，心跳由已有客户端维护；没有自建续约和持久化时间段机制。
- 断线或会话失效后永久停止当前生成器，宿主退出；重启后才能重新申请编号。
- 编号释放后可复用。分配完成后至少进入下一个毫秒才开始生成，避免同一时钟下极快重启使用同一毫秒的序号。

简化方案要求机器时钟同步，且编号复用后的时间戳晚于前任已使用的时间戳。生成器检测本进程内的时钟回拨并报错；不追踪跨重启的历史时间戳，不承诺任意跨机器时钟偏差下的唯一性。雪花 ID 不是安全令牌，认证令牌仍使用密码学随机数。

## 配置与调用点

[ops.yaml](../code/server/config/ops.yaml) 的 `zooKeeperOptions` 统一声明地址、发现根路径、`workerRootPath`、连接及会话超时，由 [server.yaml](../code/server/config/server.yaml) include 加载。App 从同一次加载结果创建 `ZooKeeperOptions` 与 `IdGeneratorOptions`；ID 和服务发现共用代理会话。独立 ID 检查也传入总入口路径。

进程基础框架使用它生成 RPC 请求 ID 和连接 ID；保留的独立存储工具使用它生成 Redis 缓存占用标记。当前节点不创建实体身份，也没有实体注册或路由流程；存储协议中的业务实体 ID 由调用方提供。

实例生命周期检查位于 [IdGeneratorLifecycleChecks.cs](../code/tests/engine/IdGeneratorLifecycleChecks.cs)，覆盖未初始化、初始化失败、取消清理、并发发号、租约监督和释放。`--ioc` 检查同容器共享、容器间隔离及释放。ID 算法及 ZooKeeper 分配检查位于 [IdGeneratorChecks.cs](../code/tests/engine/IdGeneratorChecks.cs)。[ZooKeeperProxyChecks.cs](../code/tests/engine/ZooKeeperProxyChecks.cs) 覆盖代理会话状态与进程注册发现；真实 ZK 检查另外验证共享会话、worker 释放复用和关闭代理后停发。

当前分支已移除运行 ID；控制台和验收目录沿用时间目录名，不重新引入运行批次 ID。请求和连接 ID 使用十进制文本。存量业务数据及其 ID 不因移除实体入口而改写。测试用临时目录由系统临时目录 API 创建，不属于业务 ID。

## 验证

```powershell
dotnet build code/tests/engine/TGame.EngineTests.csproj --no-restore
dotnet run --project code/tests/engine/TGame.EngineTests.csproj --no-build -- --zookeeper-session
dotnet run --project code/tests/engine/TGame.EngineTests.csproj --no-build -- --engine
```

上述为操作入口，不代表本次文档更新已执行检查。`--zookeeper-session` 只验证本地会话状态及 ID 生命周期守卫，不连接外部服务；按改动选择所需检查。真实 ZooKeeper、数据库或完整控制台联调须先按项目规范说明范围与预计耗时并获得确认，再执行相应检查，例如：

```powershell
$env:TGAME_SERVER_CONFIG = (Resolve-Path code/server/config/server.yaml).Path
dotnet run --project code/tests/engine/TGame.EngineTests.csproj -- --id-zookeeper code/server/config/server.yaml
pwsh -NoProfile -File tools/scripts/powershell/Test-ServerConsole.ps1
```

ZooKeeper 检查使用独立测试根目录，覆盖并发分配、跨节点唯一性和编号复用；算法测试的进程内生成器不向真实服务器组发送 ID。
