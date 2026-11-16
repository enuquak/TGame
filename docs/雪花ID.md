# 统一 ID 生成

引擎接口为 `IIdGenerator.GenId()`，返回 `long`。同一进程的 `ServerNode`、业务 SinglePoint 和依赖注入中的 `IIdGenerator` 共用一个实例。

```csharp
IIdGenerator idGenerator = node.IdGenerator;
long id = idGenerator.GenId();
```

业务 SinglePoint 子类可以直接使用继承的 `IdGenerator` 属性。生成器在创建节点和实体身份之前初始化；调用方不自行构造生成器或分配编号。现有协议和日志的字符串字段保持不变：

```csharp
string requestId = idGenerator.GenId().ToString(CultureInfo.InvariantCulture);
```

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

[id-generator.json](../code/server/config/id-generator.json) 统一声明 ZooKeeper 地址、编号根路径及连接/会话超时，服务发现也从这里获取 ZooKeeper 地址。

已接入请求 ID、实体 ID、连接 ID、Redis 缓存占用标记、控制台运行批次和验收请求。PowerShell 验收脚本通过探测客户端 `--ids <配置文件> <数量>` 批量调用相同生成器，不复制算法。

旧运行批次的 GUID 名称仍可用于读取历史记录，新批次使用雪花 ID。请求、实体和连接 ID 已统一为十进制文本，升级时需要同时更新服务器、验收客户端并重启整组进程。本次不改写存量数据库文档。测试用临时目录由系统临时目录 API 创建，不属于业务 ID。

## 验证

```powershell
dotnet build TGame.slnx
dotnet run --project code/tests/singlepoint/TGame.SinglePointTests.csproj
dotnet run --project code/tests/singlepoint/TGame.SinglePointTests.csproj -- --id-zookeeper code/server/config/id-generator.json
pwsh -NoProfile -File tools/scripts/powershell/Test-PingPong.ps1
pwsh -NoProfile -File tools/scripts/powershell/Test-ServerConsole.ps1
```

ZooKeeper 检查使用独立测试根目录，覆盖并发分配、跨节点唯一性和编号复用；算法测试的进程内生成器不向真实服务器组发送 ID。
