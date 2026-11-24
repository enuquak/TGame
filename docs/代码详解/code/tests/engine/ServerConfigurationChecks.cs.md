# ServerConfigurationChecks.cs 代码详解

源码：[code/tests/engine/ServerConfigurationChecks.cs](../../../../../code/tests/engine/ServerConfigurationChecks.cs)

这份测试把三个经常混在一起的问题拆开检查：连接关系是否有效、进程实例名是否规范、已经加载的配置是否独立于后来发生的文件修改和其他加载。测试会反复生成临时 YAML，再调用正式的 ServerConfiguration；没有为测试另外实现解析器。

建议先知道 [ServerConfiguration.cs](../../server/engine/configuration/ServerConfiguration.cs.md) 负责加载和校验，[ServerGroup.cs](../../server/engine/configuration/ServerGroup.cs.md) 同时定义 ServerProcess 和 ServerGroup。后者不是两份独立源码文件。本文按实际执行顺序解释输入和断言。

## 1. 一个同步测试为什么接收 TestServices

~~~csharp
internal static class ServerConfigurationChecks
{
    public static void Run(TestServices services)
~~~

internal 使类在当前程序集内可见，static 表示不需要创建测试类实例。Run 返回 void，表示没有返回结果；这里读写文件和配置校验都是同步操作，所以入口不是 async Task。

参数 services 提供共享的测试依赖。此文件只用它构造一个 Router 节点，没有调用实际拨号，也没有生成 ID；因此本段不需要先初始化真实 ZooKeeper 或 ID 租约。[测试入口](Program.cs.md) 的 --configuration 路径创建 TestServices 后直接调用它。

## 2. 先准备一份有效配置，再只改正在检查的部分

~~~csharp
var path = Path.GetTempFileName();
var defaults = Path.Combine(AppContext.BaseDirectory, "config", ServerConfiguration.FileName);
var baseline = ServerConfiguration.Load(defaults);
var document = baseline.Read<Dictionary<string, object>>();
~~~

Path.GetTempFileName 创建一个临时文件并返回路径，后续会反复覆盖它。defaults 是测试程序输出目录下的配置入口，不是按当前终端工作目录猜测源码位置。FileName 常量当前为 server.yaml。

Load(defaults) 创建完整配置对象，展开 include 并校验核心字段、数量与拓扑。随后 Read<Dictionary<string, object>>() 从已经加载的 YAML 映射反序列化出一份字典。泛型参数表示“希望读取成什么类型”：键是字符串，值用 object 接纳不同的嵌套结构。

要区分三个对象：baseline 是配置服务；document 是测试持有的可修改输入数据；path 指向下一次加载时使用的磁盘文件。Read 不把内部 root 作为可写字典直接暴露出去，修改 document 不会自动修改 baseline。

这里先读取有效基线，是为了让每个拓扑坏输入仍带着有效的环境、运行目录、ZooKeeper 配置和 processes 数量。否则“加载失败”可能仅仅因为缺少无关必填项，不能有效定位连接图的规则。

## 3. 局部函数 WriteConnections 如何捕获外部变量

~~~csharp
void WriteConnections(IReadOnlyDictionary<string, string[]> configured)
{
    document[ServerConfiguration.SectionName] = configured;
    File.WriteAllText(path, new SerializerBuilder().Build().Serialize(document));
}
~~~

这是定义在 Run 内的局部函数，不是 ServerConfiguration 的方法。它捕获外层 document 和 path，所以调用时只需传连接图。

IReadOnlyDictionary<string, string[]> 表示方法通过只读字典接口接收输入：每个字符串类型名对应一个字符串数组。例如 Base 对应 Router、DBMgr。只读接口限制此处的字典操作，不自动使所有嵌套数组深度不可变。

SectionName 是 connections。字典下标赋值把 document 中这一节替换为 configured。后面先由 SerializerBuilder 创建序列化器，再把整份字典转成 YAML 字符串，最后覆盖临时文件。

因此本测试中的写入流程是：

~~~text
测试修改独立字典
  → 测试用 YamlDotNet 序列化
  → 测试用 File.WriteAllText 写临时输入
  → 新的 ServerConfiguration 读取并校验该输入
~~~

生产配置只有从 YAML 到内存的加载接口，没有 SaveSection、配置写回或自动快照文件。不要把这个测试局部函数误认为生产配置的保存能力。

## 4. 先确认空连接映射无效，再加载完整连接图

~~~csharp
WriteConnections(new Dictionary<string, string[]>());
Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
WriteConnections(baseline.Read<Dictionary<string, string[]>>(ServerConfiguration.SectionName));
var configuration = ServerConfiguration.Load(path);
~~~

第一行写入空字典。它不同于“每种进程类型都明确写了空数组”：空字典连 Router、Base 等类型条目都没有。ValidateTargets 要求每种 ProcessType 都有连接定义，所以必须抛 InvalidDataException。

Expect 接收一个 lambda。() 表示没有参数，=> 后是将来要执行的动作。创建 lambda 时不会立即加载文件；Expect 内部调用 action() 时才执行，并捕获指定异常。

接着从 baseline 读出完整 connections，写回临时文件并重新加载。这里是测试再次准备输入，而不是给原 configuration 热更新。只有第二次加载成功，才能继续检验已加载对象的行为。

## 5. 磁盘已经变成空对象，内存拓扑仍然保留

~~~csharp
File.WriteAllText(path, "{}");
Check(configuration.GetDialTargets(ProcessType.Base).SequenceEqual(new[] { ProcessType.Router, ProcessType.DBMgr }),
    "Base must retain Router and DBMgr type dependencies after its file changes.");
Check(configuration.GetDialTargets(ProcessType.Router).Count == 0, "Router must not wait for incoming types.");
~~~

{} 是合法的空 YAML 映射，但不足以成为完整服务器配置。测试没有重新加载它，而是直接查询上一段已经加载的 configuration。

GetDialTargets(Base) 必须仍返回 Router、DBMgr。SequenceEqual 比较两个序列的元素和顺序，new[] 则让编译器推断这是 ProcessType 数组。因此本断言不仅要求“包含这两个类型”，还要求顺序与基线相同。

Router 的主动目标数应为 0。其他类型可能主动连接 Router，但入站关系不应被误算为 Router 自己必须等待的依赖。如果查询又去读取已被改成 {} 的文件，或者把入站关系混入主动列表，这些检查就可能失败。

实现上，ServerConfiguration 在构造时把 targets 校验并保存；GetDialTargets 直接访问该对象持有的结果。这里没有文件监听器，也没有每次 getter 重新读盘。

## 6. Router999 合法，不受初始启动数量的编号上限约束

~~~csharp
var node = services.CreateNode(new ServerProcess(ProcessType.Router, 999), configuration);
Check(node.FirstLinks.IsCompletedSuccessfully, "Valid identities must not be bounded by configured counts.");
var independent = ServerConfiguration.Load(defaults);
Check(!ReferenceEquals(configuration, independent), "Independent configurations share an instance.");
~~~

ServerProcess 接收进程类型和从 1 开始的编号，Name 通过 FormatName 统一生成。999 只是这个场景故意选取的较大合法编号。

process.yaml 的 processes 同时表示初始启动数量和目标类型首次连接门槛，不表示“只能存在 Router1 到 RouterN”。测试构造 Router999，要求节点接受这个身份。

Router 又没有主动依赖，所以 ServerNode 构造时就完成 FirstLinks；IsCompletedSuccessfully 表示任务正常成功，不是失败或取消。这里没有启动监听，FirstLinks 完成也不能推导地址已发布。

services.CreateNode 传入 TestServices.Startup，当前它把各类型门槛设为 1。本例无需依赖门槛，因为 Router 没有主动目标。它并没有逐个验证配置的数量计划和节点门槛绑定，相关注册关系见 [IocChecks.cs](IocChecks.cs.md)。

最后显式加载 defaults 得到 independent。ReferenceEquals 为假只证明两次加载返回不同对象；后面还会用不同拓扑内容继续证明它们没有共享可变的静态连接图。

## 7. Router 指向 Base：两点循环为什么必须拒绝

~~~csharp
var invalid = new ServerConfiguration(defaults).Read<Dictionary<string, string[]>>(ServerConfiguration.SectionName);
invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Base)];
WriteConnections(invalid);
Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
~~~

invalid 是新读出的 connections 字典，原基线仍然保留。nameof(ProcessType.Router) 取得字符串 Router，避免在正常配置构造处手写业务标识。右侧方括号是 C# 集合表达式，这里目标类型已知为 string[]，因此表示一个元素的字符串数组。

基线中 Base 已主动连接 Router。加入 Router → Base 后：

~~~text
Router 等待 Base 就绪
Base 等待 Router 就绪
~~~

生产流程要先等主动依赖达到首次门槛，再发布本端地址；循环会让两端互等，无法正常推进。ValidateAcyclic 沿主动目标递归，维护“当前递归路径”和“已完成访问”的集合；同一类型再次出现在当前路径上就抛异常。

如果此断言失败并正常加载了配置，说明初始化死锁风险被留到了运行期。

## 8. 自连接与 Gate 目标：不同原因的拒绝

~~~csharp
invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Router)];
WriteConnections(invalid);
Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Gate)];
WriteConnections(invalid);
Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
~~~

两段都替换同一个 Router 条目，所以不会把上一场景的 Router → Base 累积进来。

第一段让 Router 主动连接自身。ValidateTargets 对 peer == type 直接拒绝，不必等复杂的环遍历才发现问题。

第二段让 Router 指向 Gate。当前引擎规则禁止把 Gate 作为主动拨号目标；Gate 主动连接 Login、Base，而不是作为其他进程的此类主动目标。ValidateTargets 对 peer == ProcessType.Gate 有独立校验。

两个用例都期待 InvalidDataException，但违反的约束不同。辅助方法只检查异常类型，没有检查异常消息，因此它能观察“被拒绝”，不直接断言内部究竟在哪一行拒绝。

## 9. 三类型循环：先构造空图，再补三条边

~~~csharp
var cycle = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => Array.Empty<ProcessType>());
cycle[ProcessType.Login] = [ProcessType.Base];
cycle[ProcessType.Base] = [ProcessType.Scene];
cycle[ProcessType.Scene] = [ProcessType.Login];
WriteConnections(cycle.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.Select(type => type.ToString()).ToArray()));
Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
~~~

Enum.GetValues<ProcessType>() 取得所有枚举值。ToDictionary 的第一个 lambda 决定键，第二个决定值；这里每种类型最初都对应空数组。_ 表示第二个函数不需要用到输入值。

这种准备法保证所有类型仍有定义，但没有基线中的其他边干扰。随后仅加入 Login → Base → Scene → Login，形成长度为 3 的环。

cycle 的键和值使用枚举，WriteConnections 需要字符串，所以最后再转换：

- pair.Key.ToString() 把键变成规范类型名。
- pair.Value.Select(...) 把每个目标枚举投影为字符串。
- ToArray() 立刻收集这些字符串，得到 string[]。
- 外层 ToDictionary 形成新的字符串字典，再交给序列化函数。

这不是对生产配置新增第二套类型映射，只是测试生成输入。它补充证明环检测不应只拒绝自环和两节点互指。

## 10. 八种非法实例文本，逐个为什么无效

~~~csharp
foreach (var name in new[] { "Router", "Router0", "Router01", "router1", "Unknown1", "1", "Router2147483648", "Router-1" })
{
    Check(!ServerProcess.TryParse(name, out _), $"Invalid instance was accepted: {name}");
}
~~~

这里手写字符串是因为正在测试来自外部的坏输入，而不是正常业务代码硬编码类型。foreach 逐项调用 TryParse；out _ 丢弃解析结果，只关心返回的 bool。! 把“解析失败”转成断言应满足的 true。$"..." 是字符串插值，失败消息带上具体输入。

Router 缺少编号，不是实例名。Router0 的编号小于 1。Router01 虽然数值可读为 1，但规范化后应为 Router1，前导零使文本不一致。

router1 的大小写不符合枚举规范；Unknown1 没有对应 ProcessType；纯数字 1 缺少类型名前缀。Router2147483648 超出 int 正数范围，int.TryParse 必须失败。Router-1 不是“已知类型 + 无符号正整数”的规范形式。

TryParse 从末尾扫描 ASCII 数字，把前半段作为类型名，再验证编号与 FormatName 重建结果。重建比较正是拒绝前导零等非规范写法的重要一步。

## 11. 正常实例名与纯类型名必须分开解析

~~~csharp
Check(ServerProcess.Parse("Router123").ChannelId == 123, "Number parsing failed.");
Expect<InvalidDataException>(() => ServerProcess.ParseType("Router1"));
Expect<InvalidDataException>(() => ServerProcess.ParseType("0"));
~~~

Parse 是“必须成功”的实例解析入口，失败时抛 InvalidDataException。这里要求 Router123 的 ChannelId 为 123，确认有效输入没有被一概拒绝。

ParseType 则只接受 Router 这样的类型名，所以 Router1 不合法。字符串 0 即使可能被 Enum.TryParse 当成某个底层枚举数值，也必须因文本不等于规范类型名称而拒绝。这避免配置把枚举数值悄悄当成类型名接受。

两个解析入口服务不同位置：connections 的键和值是类型名，发现路径和链路身份则使用带编号的实例名。

## 12. 内容不同的两个配置，不能由后一次加载覆盖前一次

~~~csharp
var isolatedTargets = Enum.GetValues<ProcessType>().ToDictionary(type => type.ToString(), _ => Array.Empty<string>());
WriteConnections(isolatedTargets);
var isolated = ServerConfiguration.Load(path);
var next = ServerConfiguration.Load(defaults);
Check(!isolated.Dials(ProcessType.Scene, ProcessType.Router) && next.Dials(ProcessType.Scene, ProcessType.Router),
    "Loading another configuration changed an existing snapshot.");
~~~

isolatedTargets 为每种类型显式声明空数组。它与前面的空字典不同：类型定义完整，因此允许没有任何主动连接。

isolated 加载这个无边连接图；next 再加载正常默认配置，Scene 应主动连接 Router。最终要求旧对象返回 false，新对象返回 true。

如果内部把 targets 放在所有实例共用的静态字段中，第二次加载可能把 isolated 的查询结果也改变。这个行为断言比仅比较 ReferenceEquals 更直接地观察内容隔离。

这里“快照”指对象已经持有的内存数据，不是保存到磁盘的配置快照。启动新进程或补起进程仍会重新读取源文件，本测试不保证不同启动时刻的进程使用相同文件版本。

## 13. 辅助断言、清理和覆盖边界

~~~csharp
private static void Expect<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new Exception($"Expected {typeof(T).Name}.");
}
~~~

Action 表示无参数、无返回值的操作；泛型约束要求 T 是异常类型。出现 T 或派生异常就返回；出现其他异常则继续传播；没有异常则主动抛测试失败。因此“坏配置抛错”在这里是成功结果，不是测试崩坏。

Check 则在 bool 条件为假时抛 Exception。两者都没有特殊结果收集器；第一个未被预期处理的异常就会中断后面的场景。

~~~csharp
finally
{
    File.Delete(path);
}
~~~

进入 try 后，不论哪个检查成功或失败，都尝试删除临时文件。基线加载和局部函数声明发生在 try 之前，若准备阶段就异常，不能把这个 finally 描述成覆盖方法开始后的所有失败路径。清理代码也没有吞掉删除异常。

本文件覆盖连接图约束、示例身份文本以及加载对象的独立性；没有运行发现、监听、握手或断线恢复，也没有穷尽每个 YAML 坏输入。YAML 文档结构见 [YamlConfigurationChecks.cs](YamlConfigurationChecks.cs.md)，动态状态见 [DynamicTopologyChecks.cs](DynamicTopologyChecks.cs.md)。本文解释源码预期，没有运行这些检查。
