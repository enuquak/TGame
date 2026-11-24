# YamlConfigurationChecks.cs 代码详解

源码：[code/tests/engine/YamlConfigurationChecks.cs](../../../../../code/tests/engine/YamlConfigurationChecks.cs)

这份文件把真实 YAML 入口交给 ServerConfiguration，观察 include、锚点、重复字段、非法文档和已加载值的行为。它不连接 Mongo、Redis 或 ZooKeeper；那些配置值在这里是被解析的数据。

与 [ServerConfigurationChecks.cs](ServerConfigurationChecks.cs.md) 的区别是：后者主要改连接图，本文件主要改文件组织和 YAML 结构。两者调用同一个正式加载器，不是模拟配置加载结果。

## 1. Run 是普通同步方法，失败通过异常向外传递

~~~csharp
internal static class YamlConfigurationChecks
{
    public static void Run()
~~~

internal 限定程序集内部可见，static 表示直接按类调用。void 表示没有返回值；文件读写和反序列化在这里使用同步 API，因此没有 Task 或 await。

[测试入口](Program.cs.md) 在 --yaml-configuration 路径调用本文件，随后还会调用 SettingsChecks。这里只解释本文件，不把同一入口其他检查的覆盖范围算到这里。

测试通常以 Check 检查成功结果，以 Expect<T> 要求坏输入抛出指定异常；有一段入口检查直接使用 if 和 throw。它们都是普通 C# 控制流。

## 2. 先检查随程序复制的真实配置

~~~csharp
var source = Path.Combine(AppContext.BaseDirectory, "config", "server.yaml");
var settings = ServerConfiguration.Load(source);
if (settings.LoggingMinimumLevel != ServerLogLevel.Debug || settings.MongoDatabase != "tgame_test" ||
    settings.MongoUser != settings.RedisUser || settings.Mongo!.AuthenticationDatabase != settings.MongoDatabase)
{
    throw new Exception("The unified entry did not load nested settings and YAML aliases.");
}
~~~

AppContext.BaseDirectory 是正在执行的程序的基目录。Path.Combine 按平台路径规则组合片段；这里找的是输出目录中的 config/server.yaml，不要求终端当前目录恰好在仓库根。

Load 创建配置实例，读取入口以及 include 文件，再做严格绑定。四个条件分别检查日志级别、Mongo 数据库名、两种存储用户名一致、Mongo 认证库与数据库一致。|| 表示任一不符合都失败，并使用短路求值：前面的条件已为真时，后面的条件不再执行。

settings.Mongo! 的感叹号是 C# 空值分析抑制符，告诉编译器“这里按非空使用”。它不会运行时创建 Mongo，也不是空值检查。如果实际为 null，并且执行到这一项，访问 AuthenticationDatabase 仍会失败。

这些断言说明默认配置的嵌套字段和别名得到预期值；单凭两个值相等，不能区分它们是由别名还是重复字面量得到。因此后面会构造更有针对性的同名锚点案例。

## 3. 为这次检查创建独立目录

~~~csharp
var directory = Path.Combine(Path.GetTempPath(), $"tgame-yaml-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    var entry = Path.Combine(directory, ServerConfiguration.FileName);
    var first = Path.Combine(directory, "first.yaml");
    var second = Path.Combine(directory, "second.yaml");
~~~

Path.GetTempPath 取得系统临时目录。Guid.NewGuid 创建一个新的标识，插值中的 :N 指定无连字符格式。这样每次检查使用自己的 tgame-yaml-* 目录，后面覆盖文件不会改仓库配置。

entry 是 server.yaml，first、second 是两个同目录包含文件。这里只创建目录，三个文件由随后的 WriteAllText 生成。

try 包围临时输入的测试过程，finally 负责删除目录。Directory.CreateDirectory 发生在 try 之前，因此若目录本身无法创建，后面的 finally 尚未开始生效。

## 4. 两个文件都叫 shared 的锚点，值应各自独立

~~~csharp
File.WriteAllText(first, "mongo:\n  endpoint: &shared alpha\n  databaseName: *shared\n");
File.WriteAllText(second, "redis:\n  endpoint: &shared beta\n  userName: *shared\n");
~~~

C# 字符串中的 \n 表示换行，两个空格就是 YAML 嵌套字段的缩进。第一份写出的实际内容是：

~~~yaml
mongo:
  endpoint: &shared alpha
  databaseName: *shared
~~~

第二份是：

~~~yaml
redis:
  endpoint: &shared beta
  userName: *shared
~~~

&shared 给当前值定义锚点，*shared 引用本文件中的那个值。第一份数据库名应为 alpha，第二份用户名应为 beta。同样的锚点名称可以在不同文件中使用，但不能跨文件形成共享名字空间。

被测加载器先为每个文件创建自己的 YamlStream，再使用 CopyNode 展开该文件的别名，最后合并顶层字段。这比“把各文件文本直接拼成一个大文件再解析”更严格地保留了文件边界。

这些 alpha、beta 不是可用的数据库连接地址。本场景只需要解析存储配置；服务器基础框架允许可选存储字段在独立存储入口按需执行语义校验，因此这里没有尝试连接它们。

## 5. 入口为什么从有效配置字典修改而来

~~~csharp
var baseline = settings.Read<Dictionary<string, object>>();
baseline.Remove("mongo");
baseline.Remove("redis");
baseline["include"] = new[] { "first", "second" };
baseline[ServerConfiguration.RunDirectoryKey] = "logs/original";
File.WriteAllText(entry, new SerializerBuilder().Build().Serialize(baseline));
var configuration = new ServerConfiguration(entry);
~~~

Read<Dictionary<string, object>>() 把已加载的配置映射反序列化为一份独立字典。string 是顶层键类型，object 允许不同节保存不同结构。它不是配置内部状态的可写别名。

先删除 mongo、redis，是因为这两节将由 include 文件提供。项目不允许顶层字段重名，也没有“入口优先于 include”的覆盖规则；若不删除，加载会因重复字段失败。

new[] { "first", "second" } 由编译器推断为 string[]。include 写无扩展名的文件名，加载器在当前文件目录中追加 .yaml。RunDirectoryKey 对应 runDirectory，改成 logs/original 为后面的路径断言准备输入。

其余核心字段保留有效基线，以便本场景真的走到锚点和路径检查。SerializerBuilder 创建的是测试所用的 YAML 序列化器；File.WriteAllText 也是测试准备输入。ServerConfiguration 本身不提供配置写回接口。

## 6. 三条成功断言分别检查什么

~~~csharp
Check(configuration.Mongo!.DatabaseName == "alpha", "First document alias changed.");
Check(configuration.Redis!.UserName == "beta", "Anchor names leaked across files.");
Check(configuration.ResolveRunDirectory(directory) == Path.Combine(directory, "logs", "original"), "Relative runtime path changed.");
~~~

第一条要求 first.yaml 中 databaseName 的别名仍解析为 alpha。第二条要求 second.yaml 不会错误引用另一份文件里的 shared，而是得到 beta。它们一起检查同名锚点相互隔离。

第三条调用 ResolveRunDirectory(directory)。实现使用传入的 workingDirectory 作为相对路径基准，得到其下的 logs/original。Path.Combine 用于构造预期路径，避免把平台分隔符写死。

本例入口目录恰好也等于传入的 workingDirectory，因此断言没有通过两个不同目录来区分这两种可能的基准选择；“实际以参数为基准”来自对实现的核对。该方法只计算完整路径，不创建日志目录，也不改写 YAML。

## 7. 删除源 include 后，旧对象能读，新加载必须失败

~~~csharp
File.Delete(first);
File.Delete(second);
Check(configuration.Redis!.UserName == "beta", "Loaded settings still depend on source include files.");
Expect<FileNotFoundException>(() => new ServerConfiguration(entry));
~~~

configuration 在删除前已经完成加载。它持有反序列化出的配置值，所以查询 Redis.UserName 应仍为 beta。如果每次读取属性都重读 include，这里就会丢失值或抛文件异常。

最后一句却创建全新配置对象，必须重新读 entry 中指定的 include。first、second 已不存在，因此预期 FileNotFoundException。

两个检查一起说明：

~~~text
已加载对象：继续使用内存中已有值
新建配置对象：重新读取源文件，缺文件就失败
~~~

这里没有自动保存展开后的配置快照，也没有热重载。同一进程运行期间使用自己的已加载值，补起或新进程仍读源文件。不能把旧对象存活误解为“文件删除后所有未来加载仍能成功”。

## 8. 包含文件之间不能覆盖同名顶层字段

~~~csharp
File.WriteAllText(first, "shared: first\n");
File.WriteAllText(second, "shared: second\n");
Expect<InvalidDataException>(() => new ServerConfiguration(entry));
~~~

测试重新创建两份文件，故意都定义顶层 shared。entry 仍包含 first 和 second，读取 first 后，合并结果已有 shared；继续合并 second 时发现同键，必须失败。

这里的 shared 是普通顶层键，与上一节 &shared 锚点名称不是同一种东西。锚点可跨文件同名而互不干扰，顶层配置键在合并后的统一映射中却不能重名。

加载器 Merge 用 ContainsKey 检测冲突，不实行“后读文件覆盖前读文件”。测试只检查 InvalidDataException，不检查消息；但结合加载顺序，预期是合并阶段先发现冲突。

## 9. 入口也不能覆盖 include 的字段

~~~csharp
File.WriteAllText(entry, "include: [first]\nshared: entry\n");
Expect<InvalidDataException>(() => new ServerConfiguration(entry));
~~~

first.yaml 仍有 shared: first。入口先加载 include，再合并自己的 shared: entry。重复键必须被拒绝，入口没有更高优先级。

因此 include 的顺序只是读取顺序，不是配置覆盖优先级。若要改变某项值，应修改其唯一声明位置，而不能依靠另一份文件后写覆盖。

## 10. 循环逐份覆盖入口：八类坏输入逐项阅读

~~~csharp
foreach (var invalid in new[] { "include: first", "include: [../first]", "include: [first.yaml]", "include: [first, first]",
    "value: 1\nvalue: 2", "values:\n  value: 1\n  value: 2", "[one, two]", "---\na: 1\n---\nb: 2" })
{
    File.WriteAllText(entry, invalid);
    Expect<InvalidDataException>(() => new ServerConfiguration(entry));
}
~~~

foreach 每次取一个字符串，覆盖整个 entry，再创建新配置。每轮都从磁盘重新开始，不是给上一轮 configuration 对象应用修改。

第一种 include: first 是标量；项目要求 include 必须为 YAML 列表，即使只包含一个文件，也要写 [first] 或等价的块序列形式。Load 检查 includes 是否为 YamlSequenceNode。

第二种 ../first 包含目录分隔符，试图跳到父目录，不符合“同目录文件名”的约定。第三种 first.yaml 带扩展名，加载器不允许，因为它自己统一追加 .yaml。IsIncludeName 会检查扩展名、分隔符和无效文件名字符。

第四种 [first, first] 把同一个名字重复包含两次。名称集合在当前 include 列表内发现重复就拒绝，不让相同文件悄悄重复参与合并。

第五种在顶层写两次 value，第六种在 values 下的同一映射中写两次 value。两者都要求重复键失败，说明严格性不能只停留在合并不同文件的顶层。YAML 解析或随后复制、绑定的重复检查负责暴露这类无歧义约束。

第七种 [one, two] 把根节点写成列表；服务器配置根必须是映射。第八种用 --- 写两个独立 YAML 文档，虽然 YAML 格式可以表示多文档流，本加载器要求恰好一个文档。

这些短输入同时缺少完整服务器配置，Expect 又只检查同一种 InvalidDataException。因此用例结果本身无法证明每个错误一定在预想的结构校验点被捕获；本文指出的对应位置来自源码核对，不能将这一组检查夸大为严格定位每条错误原因的测试。

## 11. include 循环：server → first → server

~~~csharp
File.WriteAllText(entry, "include: [first]\n");
File.WriteAllText(first, "include: [server]\n");
Expect<InvalidDataException>(() => new ServerConfiguration(entry));
~~~

加载 entry 时把自己的完整路径加入 loading 集合，继续加载 first；first 又请求同目录的 server.yaml。这个路径已经在当前递归链里，loading.Add 返回 false，于是报循环包含。

loading 是当前加载路径集合，不是永久“已读过”的缓存。实现的 finally 会在返回一层时移除该路径。检测循环依靠当前递归链，而非简单地禁止所有位置再次出现某个路径。

本用例模拟文件引用的循环。它与后面的 YAML 别名循环属于两个不同层级的问题。

## 12. 跨文件锚点引用为什么无效

~~~csharp
File.WriteAllText(entry, "include: [first, second]\n");
File.WriteAllText(first, "first: &onlyHere value\n");
File.WriteAllText(second, "second: *onlyHere\n");
Expect<InvalidDataException>(() => new ServerConfiguration(entry));
~~~

first 定义 onlyHere，second 只引用它，却没有在自己的文件中定义。因为文件各自解析，second 的解析器看不到 first 的锚点，所以必须失败。

前面的同名 shared 测试要求“不同文件各自定义同名锚点可以成功”；本段要求“借用别的文件的锚点必须失败”。这两面共同解释了锚点的文件作用域。

加载器将 YamlException 包装成 InvalidDataException，对调用方提供统一的配置格式错误。它没有把跨文件引用延迟到顶层合并后再解析。

## 13. 递归别名不能无限展开

~~~csharp
File.WriteAllText(entry, "value: &recursive [*recursive]\n");
Expect<InvalidDataException>(() => new ServerConfiguration(entry));
~~~

value 是一个序列，锚点 recursive 指向这个序列，序列中的别名又引用它自身。这不是重复使用普通值，而是一个循环对象图。

CopyNode 在进入节点时把节点对象放入 visiting，按引用身份比较；如果沿当前展开路径再次遇到同一对象，立即失败。它还有最大嵌套深度保护。这样不会因为复制别名而无限递归或耗尽调用栈。

本段只提供了一个自引用案例，没有测试全部深度边界、多节点别名循环或所有 YAML 语法变体。

## 14. Check 和 Expect：异常怎样成为测试证据

~~~csharp
private static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}
~~~

条件必须为 true。! 是逻辑取反；如果 condition 为假，方法抛出带场景说明的异常，调用方不会继续正常执行后续检查。

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

Action 是无参数、无返回值的委托。() => new ServerConfiguration(entry) 把构造动作交给它；创建出的对象不需要保存，因为这里只关心是否失败。

T 是调用时指定的异常类型，约束要求它继承 Exception。catch (T) 也接受派生类型；遇到其他类型不处理。没有任何异常时，最后的 throw 让“错误输入被接受”成为明确失败。

文件缺失场景使用 FileNotFoundException，结构错误使用 InvalidDataException，因此不能把任意异常都当成预期结果。但它们没有验证异常消息、内部原因或精确类型相等。

## 15. 最终清理与没有覆盖的事情

~~~csharp
finally
{
    Directory.Delete(directory, recursive: true);
}

Console.WriteLine("PASS unified YAML, independent anchors, strict includes/duplicates, invalid documents and immutable loaded settings.");
~~~

recursive: true 是命名参数，表示连同临时子文件一起删除。本次路径由临时根与新 GUID 组成，删除目标是这次创建的独立目录，不是源码配置目录。

finally 在测试体异常时也尝试清理，清理错误没有被吞掉。PASS 位于 finally 之后，所以正常打印前要求这些场景和目录删除都已完成。

本文件检查加载器的若干文件边界和结构规则，不能替代所有字段语义校验、存储连通性、配置热更新或启动器行为测试。生产配置仅从源 YAML 加载到内存；本文件主动写临时文件只是构造测试数据。本文基于源码解释流程，没有执行检查。
