# 统一 ID 生成

引擎统一入口为 `IdGenerator.GenId()`，返回使用 `CultureInfo.InvariantCulture` 格式化的十进制 `string`。同一进程的节点、SinglePoint 和业务代码共用唯一生成器，不再通过构造函数或初始化参数传递生成器。

```csharp
using TGame.Server.Engine;

string id = IdGenerator.GenId();
```

宿主在创建节点和实体身份之前，先初始化 ZooKeeper 代理，再初始化 ID 生成器。代理持有进程唯一的连接和会话，生成器通过代理申请编号。返回对象用于管理生命周期：

```csharp
await using var zooKeeperLifetime = await ZooKeeperProxy.InitializeAsync(zooKeeperOptions);
await using var idLifetime = await IdGenerator.InitializeAsync(idOptions);
using var node = new ServerNode(process.Name, group);
```

`await using` 确保节点和服务退出后先释放生成器的 worker 租约，最后关闭 ZooKeeper 代理。宿主通过 `ZooKeeperProxy.Instance.SessionLost` 监督共享会话；`IdGenerator.LeaseLost` 指向同一失效通知，业务代码只需调用 `IdGenerator.GenId()`。现有协议和日志的字符串字段直接使用返回值。

未初始化、初始化尚未完成或已释放时调用会明确报错；已有活动实例时重复初始化也会报错。生命周期由宿主按顺序管理，不支持并发初始化或并发释放，也不为此设置锁。生命周期对象释放后不再使用；下一次初始化须等上一次完整释放。初始化失败或取消不会发布半初始化实例，取消时已创建的生成器会被释放。正常服务器仍在租约丢失后退出，由重启重新申请编号。

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

[id-generator.json](../code/server/config/id-generator.json) 仅声明编号根路径。[zookeeper.json](../code/server/config/zookeeper.json) 统一声明连接和会话超时。ZooKeeper 地址与进程发现根路径继续由宿主 ServerSettings 提供，ID 分配和服务发现共用同一个代理会话。宿主将配置显式传给引擎，引擎不读取业务配置。

已接入请求 ID、实体 ID、连接 ID 和 Redis 缓存占用标记。`ServerSinglePoint` 创建时通过属性初始化表达式调用 `IdGenerator.GenId()`，直接持有 `EntityId` 和本地实体集合；`ServerNode` 不提前生成该身份。生成身份不会提前登记实体，实体注册仍由 SinglePoint 自身初始化发起。

单例生命周期检查位于 [IdGeneratorLifecycleChecks.cs](../code/tests/singlepoint/IdGeneratorLifecycleChecks.cs)，覆盖未初始化、初始化失败、取消清理、顺序初始化、并发发号、租约监督和释放。ID 算法及 ZooKeeper 分配检查位于 [IdGeneratorChecks.cs](../code/tests/singlepoint/IdGeneratorChecks.cs)。[ZooKeeperProxyChecks.cs](../code/tests/singlepoint/ZooKeeperProxyChecks.cs) 覆盖代理会话状态与进程注册发现；真实 ZK 检查另外验证共享会话、worker 释放复用和关闭代理后停发。

当前分支已移除运行 ID；控制台和验收目录沿用时间目录名，不重新引入运行批次 ID。请求、实体和连接 ID 已统一为十进制文本，升级时需要统一更新服务器并重启整组进程。本次不改写存量数据库文档。测试用临时目录由系统临时目录 API 创建，不属于业务 ID。

## 验证

```powershell
dotnet build code/tests/singlepoint/TGame.SinglePointTests.csproj --no-restore
dotnet run --project code/tests/singlepoint/TGame.SinglePointTests.csproj --no-build -- --zookeeper-session
dotnet run --project code/tests/singlepoint/TGame.SinglePointTests.csproj --no-build -- --node
```

上述小范围检查无需连接外部服务。真实 ZooKeeper、数据库或完整控制台联调须先按项目规范说明范围并获得确认，再执行相应检查，例如：

```powershell
$env:TGAME_SERVER_CONFIG = (Resolve-Path code/server/config/server.local.yaml).Path
dotnet run --project code/tests/singlepoint/TGame.SinglePointTests.csproj -- --id-zookeeper code/server/config/id-generator.json
pwsh -NoProfile -File tools/scripts/powershell/Test-ServerConsole.ps1
```

ZooKeeper 检查使用独立测试根目录，覆盖并发分配、跨节点唯一性和编号复用；算法测试的进程内生成器不向真实服务器组发送 ID。
