# LogEventJson.cs 代码详解

源码：[code/server/engine/logging/LogEventJson.cs](../../../../../../code/server/engine/logging/LogEventJson.cs)

这份文件负责把一次日志调用变成 JSON 字符串。它不选择输出目标、不打开文件，也不判断最低级别；这些工作在 [GameLog.cs](../GameLog.cs.md) 中完成。阅读时可以把它看作一次同步的数据转换。

## 1. 静态工具类与方法签名

```csharp
using System.Text.Json;

namespace TGame.Logging;

public static class LogEventJson
{
    public static string Serialize(ServerLogLevel level, string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null, string? instance = null)
```

文件顶部的 `using` 引入命名空间，使后面可以直接写 `JsonSerializer`。它不是资源清理用的 `using var`。

`static class` 表示这是工具类，不能用 `new LogEventJson()` 创建实例。静态方法直接通过 `LogEventJson.Serialize(...)` 调用。`string` 返回类型表示方法调用成功时立即得到一段文本；这里没有 `Task`，也没有异步等待。

前三个参数必传：级别 `level`、事件名 `ev`、进程类型文字 `role`。其余参数的 `string?` 表示可以为空引用，`= null` 允许调用方省略。

例如以下为调用示意：

```csharp
string json = LogEventJson.Serialize(
    ServerLogLevel.Info,
    ServerLogEvents.Listen,
    nameof(ProcessType.Login),
    detail: "开始监听",
    instance: "Login1");
```

`detail:` 和 `instance:` 是命名参数，可以跳过中间不使用的可选参数。`nameof(ProcessType.Login)` 在编译时得到字符串 `"Login"`，并不创建进程。

## 2. 先构造字典，再序列化

方法主体如下：

```csharp
return JsonSerializer.Serialize(new Dictionary<string, object?>
{
    [ServerLogFields.Time] = DateTimeOffset.UtcNow,
    [ServerLogFields.Level] = level.ToString().ToUpperInvariant(),
    [ServerLogFields.Event] = ev,
    [ServerLogFields.Role] = role,
    [ServerLogFields.Instance] = instance ?? role,
    [ServerLogFields.Pid] = Environment.ProcessId,
    [ServerLogFields.Peer] = peer,
    [ServerLogFields.Id] = id,
    [ServerLogFields.Entity] = entity,
    [ServerLogFields.Link] = link,
    [ServerLogFields.Detail] = detail
});
```

`Dictionary<string, object?>` 是泛型字典：键必须是字符串，值可以是不同类型的对象，也可以是 `null`。这允许时间、整数 PID 和多个字符串放在同一个对象中。值类型（如整数）放进 `object` 槽位时会装箱；序列化器仍按它实际的类型输出 JSON。

`[某个键] = 某个值` 是索引初始化器。这里的方括号不是读取数组，而是在创建字典时为指定键赋值。`ServerLogFields.Time` 的值是 `"time"`，所以第一项等价于建立一个名为 `time` 的属性。

执行顺序是先计算字典各项并完成字典创建，再把整个字典传给 `JsonSerializer.Serialize`，最后返回序列化结果。方法没有保存字典供下次复用；下一条日志会得到自己的时间和字段集合。

## 3. 时间、级别与 PID 是怎样来的

```csharp
[ServerLogFields.Time] = DateTimeOffset.UtcNow,
[ServerLogFields.Level] = level.ToString().ToUpperInvariant(),
```

`DateTimeOffset.UtcNow` 取得构造这条记录时的 UTC 时间。它是带偏移信息的时间值，序列化后表现为 JSON 字符串；不是调用方传进来的业务事件时间，也不是操作系统进程启动时间。

级别先从枚举转成名称，例如 `Info`，再转成 `INFO`。`ToUpperInvariant()` 使用固定文化规则，避免运行机器的语言设置影响协议文本。

`Environment.ProcessId` 返回当前操作系统进程的整数 PID。因此 JSON 的 `pid` 是数字，不是带引号的字符串，也不是 `Login1` 里的 `1`。实例编号与操作系统 PID 属于两套不同的身份。

本函数不检查未知枚举值。正常路径中 `GameLog.Write` 已通过 `ToSerilogLevel` 拒绝非法值；如果其他调用方直接调用本工具，则不能把“可以被序列化”当作“日志级别已验证”。

## 4. role 与 instance 为什么保留两个字段

```csharp
[ServerLogFields.Role] = role,
[ServerLogFields.Instance] = instance ?? role,
```

`role` 是类型，例如 `Login`；`instance` 是具体实例，例如 `Login1`。`??` 是空合并运算符：左边不是 `null` 就用左边，只有左边为 `null` 才使用右边。

服务器刚创建日志时还没有从 ZooKeeper 认领实例，`GameLog.processName` 为 `null`。这时记录的 `instance` 回退为 `role`，所以启动早期两者都可能是 `Login`。认领并调用 `OpenServer` 后，记录则是 `role = Login`、`instance = Login1`。

这个回退只判断 `null`；空字符串 `""` 不会触发回退。方法也没有解析或校验实例名称，身份正确性来自上游的 [ServerGroup.cs](../configuration/ServerGroup.cs.md) 与认领过程。

## 5. 可选字段为什么仍出现在 JSON 中

`peer`、`id`、`entity`、`link`、`detail` 都无条件加入字典。未提供的参数值为 `null`，在当前默认序列化设置下会输出 JSON 的 `null`，而不是省略这个键，也不是字符串 `"null"`。

这些字段的用途见 [ServerLogProtocol.cs](ServerLogProtocol.cs.md)。其中保留 `entity` 字段不代表当前启动流程已经创建实体；当前运行的是进程基础框架。

下面是省略真实时间、用假定 PID 表达结构的示意，并非本轮运行结果：

```json
{"time":"2026-11-25T00:00:00+00:00","level":"INFO","ev":"LISTEN","role":"Login","instance":"Login1","pid":4020,"peer":null,"id":null,"entity":null,"link":null,"detail":"开始监听"}
```

实际序列化可能把中文等字符写成 Unicode 转义。转义后的 JSON 解析回字符串仍是原内容；不应把原始 JSON 的视觉表现与解析出的文本混为一谈。

## 6. 含引号和换行的详情仍是一条记录

调用方可能传入异常堆栈或多行详情，例如以下示意字符串：

```csharp
string detail = "第一行\n第二行：\"失败\"";
```

其中 `\n` 在 C# 字符串值里代表一个真实换行，`\"` 代表一个双引号。序列化器把这些内容转为合法的 JSON 转义，不会直接把原始换行插入 JSON 记录中，因此返回值可以作为一条物理日志行保存。

这里使用默认选项，不启用缩进。`GameLog` 或控制台输出最后再补一个记录分隔换行。“详情本身有换行”和“文件中的记录分隔换行”必须区分。

[LogLevelChecks.cs](../../../tests/engine/LogLevelChecks.cs.md) 用包含引号、中文和换行的详情写文件并镜像到标准错误，逐行解析并核对两边完全相同。但该检查没有逐个断言本函数的全部可选字段或所有转义字符。

## 7. 状态与失败传播

这个类不持有 logger、流或可变的全局字典，所以不用释放资源。返回的字符串由调用方决定写到哪里；生成 JSON 成功不等于文件写入成功。

方法没有 `try/catch`。如果构造或序列化过程中出现异常，会直接传回调用方；本函数不吞异常，也不返回一条伪造的成功记录。正常输入的类型都很简单，但这一事实仍不能替代上游的身份、配置与生命周期校验。
