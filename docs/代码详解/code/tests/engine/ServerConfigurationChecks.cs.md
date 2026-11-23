# ServerConfigurationChecks.cs 代码详解

源码：[code/tests/engine/ServerConfigurationChecks.cs](../../../../../code/tests/engine/ServerConfigurationChecks.cs)

## 职责与依赖

`Run(services)` 检查类型连接拓扑、规范进程名和已加载配置对象的隔离。它读取测试输出的完整默认配置，用 `Read<Dictionary<string, object>>()` 取得独立映射。局部函数 `WriteConnections` 替换这份映射的 `connections`，再通过 `SerializerBuilder` 序列化并写入临时文件；生产配置类型不提供写回接口。`TestServices` 仅用于构造无依赖 Router 节点，不需要初始化真实服务。

建议先看 [ServerConfiguration.cs](../../server/engine/configuration/ServerConfiguration.cs.md) 的构造函数和 `Dials`，再按本文件 `Run` 的顺序读。这里没有测试框架特有的属性标记：普通方法中的断言失败就抛异常，调用方观察到异常便判定失败。

## 准备阶段：区分文件、字典与已加载对象

| 变量 | 保存什么 | 是否随写文件自动变化 |
| --- | --- | --- |
| `defaults` | 测试构建输出中的配置入口路径 | 只是路径 |
| `baseline` | 从默认入口加载的完整配置对象 | 不会 |
| `document` | 由 `Read` 反序列化得到的可修改字典 | 只在测试明确修改时变化 |
| `path` | `Path.GetTempFileName()` 创建的独立临时文件 | 后续测试反复覆盖这个文件 |
| `configuration` | 从当时的临时文件加载的配置实例 | 后续覆盖文件不会修改它 |

`Dictionary<string, object>` 表示顶层键是字符串，各节的值可以是数量映射、列表或嵌套对象。它不是生产配置内部 `root` 的可写引用。测试先复制出输入数据，再构造下一次加载时的文件。

```csharp
void WriteConnections(IReadOnlyDictionary<string, string[]> configured)
{
    document[ServerConfiguration.SectionName] = configured;
    File.WriteAllText(path, new SerializerBuilder().Build().Serialize(document));
}
```

这是 `Run` 内部的局部函数。它捕获外层的 `document` 和 `path`，所以调用时只传新的连接图。函数只替换 `connections`，其他核心字段和数量配置保持有效，让后续失败尽量指向正在测试的连接规则，而不是无关的缺字段错误。

例如参数中 `Base` 对应 `[Router, DBMgr]`，序列化后就是 YAML 的 `Base: [Router, DBMgr]`。这里测试自己调用文件 API 写入临时输入，不表示生产 `ServerConfiguration` 具有保存功能。

## 拓扑与独立性检查

空 `connections` 映射必须被拒绝。加载完整配置后，把磁盘文件改成空对象，已经持有的配置仍应保留 Base 到 Router、DBMgr 的主动依赖；Router 的主动目标为空。以 `Router999` 创建节点可立即完成首连，说明合法实例编号不受初始数量上限约束。再次加载默认文件必须得到独立对象。

随后修改 Router 的目标：指向 Base 会与现有 Base→Router 形成环；指向自身是自环；指向 Gate 违反禁止以 Gate 为主动目标的规则，三者均应拒绝。另将 Login→Base→Scene→Login 的三节点循环写进临时配置，再加载验证环检测。

最后构造没有任何主动连接的独立配置，再加载默认配置，要求前者仍不具有 Scene→Router，后者具有该关系，证明加载新配置不会改写已存在实例。

可以把这部分按“动作与期望”逐项对照：

| 动作 | 预期 | 证明的规则 |
| --- | --- | --- |
| 写入空连接映射 | 加载抛 `InvalidDataException` | 所有进程类型都必须有配置项，空目标列表与缺少类型不是一回事 |
| 成功加载后将文件写为 `{}` | 已持有对象仍有原来的 Base 依赖 | 配置值在加载后保存在内存，不是每次查询都重读文件 |
| 创建 `Router999` | `FirstLinks` 已成功完成 | Router 无主动依赖，且启动数量不是实例编号的合法上限 |
| 再次加载默认配置 | `ReferenceEquals` 为 false | 两次显式加载产生独立对象 |
| 添加 Router→Base | 加载失败 | 与原 Base→Router 组成环 |
| 添加 Router→Router | 加载失败 | 不允许自依赖 |
| 添加 Router→Gate | 加载失败 | Gate 不能作为主动拨号目标 |
| 配置 Login→Base→Scene→Login | 加载失败 | 环检查覆盖多个类型组成的环 |

`SequenceEqual` 在这里还会比较目标的排列顺序；测试要求默认 Base 目标依次为 Router、DBMgr，而不只是“集合里有这两个类型”。`ReferenceEquals` 比较对象身份，不比较配置内容是否相同。

最后的隔离检查比单纯检查对象身份更进一步：同时持有 `isolated` 和 `next`，分别查询 Scene→Router，要求一个为 false、另一个为 true。如果配置放在某个被后一次加载覆盖的静态字典里，这个断言就会失败。

## 身份文本检查

`ServerProcess.TryParse` 必须拒绝没有编号的 Router、零编号、前导零、大小写不符、未知类型、纯数字、编号溢出和负数。`Router123` 应解析出编号 `123`；`ParseType` 不接受带编号的实例名或数字枚举文本，维持类型名与实例名的边界。

```csharp
Check(!ServerProcess.TryParse(name, out _), $"Invalid instance was accepted: {name}");
```

`out _` 表示不需要解析出的对象，只关心是否成功。前面的 `!` 将“解析失败”变成测试应满足的 true。`nameof(ProcessType.Router)` 则在编译时取得规范名字，避免测试自己再维护一套角色字符串。

`Expect<T>(Action action) where T : Exception` 接收一段待执行的同步操作。例如：

```csharp
Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
```

Lambda 此时只是传入函数，直到 `Expect` 内调用 `action()` 才真正加载。捕获到指定类型的异常才返回；没有异常时主动抛测试失败，其他异常类型自然向上传播，不会被当成成功。

## 清理与限制

所有错误通过 `Check`、`Expect<T>` 转为检查失败；`finally` 删除临时文件。这里不启动连接，也不检验发现或首连数量达到过程；它验证加载时约束及对象独立性，实际就绪状态变化由连接测试负责。

本测试中的“修改配置文件后旧对象保持不变”只描述已经加载的对象。启动新进程或补起进程会再次加载源配置，并不保证源文件修改前后的进程使用相同版本。对应的启动数量、动态连接行为可回看已读的 [StartupReadinessChecks.cs](StartupReadinessChecks.cs.md)。
