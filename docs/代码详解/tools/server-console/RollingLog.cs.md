# RollingLog.cs 代码详解

[源码](../../../../tools/server-console/RollingLog.cs)

`RollingLog` 从持续追加、按小时和大小滚动的日志文件里读出完整行。它记住上次读到的文件和字节偏移，允许一行跨多次刷新才写完，又限制每次最多读取 256 KiB，避免控制台重开时一口气回放大量日志。

[ServerInstance](ServerInstance.cs.md) 给它传入某次进程生命期的目录和文件前缀，并把 `AddLine` 作为接收函数。读取器只处理文件、字节和行边界；JSON 是否合法、事件如何改变状态，由调用方解释。日志命名来源可对照 [GameLog 源码](../../../../code/server/engine/GameLog.cs)。

## 1. 主构造参数和长期保留的状态

```csharp
internal sealed class RollingLog(string directory, string prefix)
{
    private const int BufferSizeBytes = 16 * 1024;
    private const int ReadBudgetBytes = 256 * 1024;

    private readonly byte[] buffer = new byte[BufferSizeBytes];
    private readonly MemoryStream partial = new();
    private (string Hour, int Part)? current;
    private long offset;
```

`directory`、`prefix` 是主构造函数参数，后续方法直接使用。典型前缀为 `<PID>-<启动UTC ticks>-`，因此只读某次进程生命期，而不是混读同角色的所有历史文件。

`new byte[...]` 分配 16 KiB 缓冲区。`readonly` 使字段不能换指向另一数组，但数组内容可以反复被 `Read` 写入。

`MemoryStream partial = new()` 创建内存流，用于保存尚未遇到换行的字节。这里 `new()` 根据字段类型推断构造类型。它跨多次 `ReadAvailable` 调用保存数据，不是每一轮重新创建。

`(string Hour, int Part)?` 是可空元组，含命名成员 Hour 和 Part。首次读取前 `current` 没值，之后保存当前小时及该小时内的大小分片编号。`offset` 是当前分片已经读过的字节数，不是已读行数或字符数。

| 状态 | 何时改变 |
| --- | --- |
| `current` | 切到新分片或处理当前文件截断时设置 |
| `offset` | 每次读到字节后增加，换文件/截断时归零 |
| `partial` | 没换行时累积，完成一行或换分片时清空 |
| `buffer` | 每次 FileStream.Read 填入本次数据，旧尾部不保证有效 |
| 方法内 `budget` | 每次调用重设为 256 KiB，各文件共用 |

读取器不长期持有磁盘文件句柄。每次刷新为需要读取的文件打开流，用完即关闭；保留的是偏移和未完成行的字节。

## 2. ReadAvailable 的签名与返回值

```csharp
internal bool ReadAvailable(Action<string> accept)
```

`Action<string>` 是接收一个字符串、不返回值的委托。调用方传 `AddLine` 方法组，读取器执行 `accept(line)` 时，就同步进入调用方的那段方法。

此方法没有 `async`，读磁盘与执行回调都发生在调用线程上。正常控制台路径由 UI 计时器调用，所以它用字节预算控制一次刷新做多少读取工作。

返回 true 表示本次没有因字节预算用尽而停下，并已遍历本次可见文件；false 表示预算用完，需要下一轮继续。它不表示“服务器不再写日志”，也不保证返回时没有刚刚新增的文件或字节。

```csharp
if (!Directory.Exists(directory))
{
    return true;
}
```

服务器可能还没创建日志目录。此时没有可读文件，正常返回 true，而不是把目录尚不存在当成启动失败。枚举开始后目录又被移除则可能在后面抛 I/O 异常，交给 `ServerInstance` 的特定重试处理。

## 3. RollKey 把文件名转换成可排序的键

先看方法底部的解析函数：

```csharp
private (string Hour, int Part) RollKey(string path)
{
    var suffix = Path.GetFileNameWithoutExtension(path)[prefix.Length..];
    int split = suffix.IndexOf('_');
    return split >= 0 && int.TryParse(suffix.AsSpan(split + 1), out int part) ? (suffix[..split], part) : (suffix, 0);
}
```

`Path.GetFileNameWithoutExtension` 去掉目录和 `.log`。`[prefix.Length..]` 是范围切片：从前缀之后直到字符串末尾。此处依赖文件来自前缀匹配的枚举结果，不是任意文件名输入的通用验证器。

`IndexOf('_')` 找到大小滚动后缀分隔符，找不到返回 -1。只有找到分隔符才调用 `TryParse`，把下划线右边的文本解析为 int，并通过 `out int part` 接收结果。

条件成立时返回 `(下划线左边小时串, 数字分片号)`；否则返回 `(整个后缀, 0)`。三元表达式两边都是同一元组类型。

以下是按当前命名约定构造的例子：

| 去掉生命期前缀后的后缀 | RollKey 结果 |
| --- | --- |
| `2026112501` | `("2026112501", 0)` |
| `2026112501_999` | `("2026112501", 999)` |
| `2026112501_1000` | `("2026112501", 1000)` |
| `2026112502` | `("2026112502", 0)` |

该函数不严格验证小时格式。分片数字解析失败时走兜底元组，不是抛“坏日志文件名”的专门异常。其排序设计以 [GameLog](../../../../code/server/engine/GameLog.cs) 使用的 Serilog 命名规则为前提。

## 4. 为什么先按小时字符串，再按分片数字排序

```csharp
var files = Directory.EnumerateFiles(directory, prefix + "*.log")
    .Select(path => (Path: path, Key: RollKey(path)))
    .OrderBy(file => file.Key.Hour, StringComparer.Ordinal).ThenBy(file => file.Key.Part);
```

`EnumerateFiles` 仅枚举这个目录下匹配前缀的 `.log` 文件；没有递归读取子目录。

`Select` 为路径附上 RollKey，形成含 Path 和 Key 的元组。`OrderBy` 按 Hour 使用序号比较，固定宽度 `yyyyMMddHH` 的字典顺序就是时间顺序。`ThenBy` 在相同小时内按整数 Part 排序。

如果直接按完整文件名字典序，`_1000` 与 `_999` 的顺序可能不符合数字顺序。解析成整数后明确是 999 在前、1000 在后。

LINQ 查询在 foreach 枚举时实际工作，排序也需要处理本次枚举到的候选项。256 KiB 预算限制的是读取字节量，不给目录枚举和排序本身设置硬性时间上限。

## 5. 已经过去的分片直接跳过

```csharp
int budget = ReadBudgetBytes;
foreach (var file in files)
{
    if (current is { } previous &&
        (string.CompareOrdinal(file.Key.Hour, previous.Hour) < 0 ||
         file.Key.Hour == previous.Hour && file.Key.Part < previous.Part))
    {
        continue;
    }
```

`current is { } previous` 匹配非空元组，并把里面的值绑定为 previous。首次读取 current 为 null，不进入这项跳过判断。

小时更早，或者同小时但分片编号更小，就跳过。`&&` 比 `||` 结合更紧，这里括号还明确了“任一种更早情况”作为整体。代码保留当前分片，因为它可能继续追加内容。

由此也能看出设计边界：一旦转到后面的分片，后来又向旧分片追加的字节不会重新读取。正常日志生产者应按滚动顺序前进，而不是反复回写旧分片。

## 6. Open 的共享模式与文件消失竞争

```csharp
private static FileStream? Open(string path)
{
    try
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }
    catch (FileNotFoundException)
    {
        return null;
    }
}
```

`FileMode.Open` 要求打开已有文件，不负责新建；`FileAccess.Read` 只读。`FileShare.ReadWrite | FileShare.Delete` 用按位或组合共享标志，允许其他句柄在符合系统共享规则时读写、删除这个文件，适合日志边写边滚动。

枚举后到打开前，文件可能已被滚动清理，所以 `FileNotFoundException` 返回 null，外层继续下一个分片。权限等其他异常不会被这个 catch 吞掉。

```csharp
using var stream = Open(file.Path);
if (stream == null)
{
    continue;
}
```

`using var` 让当前 foreach 迭代作用域退出时释放流：读完、continue、return 或异常都会进行相应清理。空流时也可以安全离开，不需要手写 finally。

## 7. 换文件或截断时为什么丢弃 partial

```csharp
if (current != file.Key || stream.Length < offset)
{
    current = file.Key;
    offset = 0;
    partial.SetLength(0);
}

stream.Position = offset;
```

可空元组与文件键不同，表示首次读取或切到另一个分片，需要从零开始。文件仍是当前分片但 `Length < offset`，说明上次读过的位置已超出当前长度，按截断处理，也从头开始。

`partial.SetLength(0)` 丢弃旧半行，避免把一个分片末尾残缺字节与另一个文件开头拼成伪造记录。正常跨刷新仍读同一个未截断文件时，不清空 partial，才能延续那一行。

例如上次 offset 为 1000，这次文件长度变成 200，就重置。若文件被替换或截断后迅速长到不小于旧 offset，此条件本身不能识别全部这种历史变化；读取器没有保存文件系统文件 ID。

`stream.Position = offset` 定位到上次已读字节之后，避免每次从文件头重复读取。

## 8. 每块读取先更新游标和预算

```csharp
int count;
while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, budget))) > 0)
{
    offset += count;
    budget -= count;
    int start = 0;
```

`stream.Read` 把字节放进 buffer 从索引 0 开始的位置，最多读 `Math.Min(16 KiB, 剩余预算)`。返回 count 是实际读取量，可能少于请求量；零表示这次已读到当前文件末尾。

括号里的赋值表达式先把返回值赋给 count，再与 0 比较。每次循环只允许使用前 count 个字节，buffer 后面的内容可能是上次读取遗留数据。

offset 与 budget 都按实际字节数更新，不按 UTF-8 字符数。一个中文字符可占多个字节，因此不能拿字符串长度代替这里的 count。

## 9. 在一块字节里找出所有完整行

```csharp
for (int end = Array.IndexOf(buffer, (byte)'\n', 0, count); end >= 0;
     end = Array.IndexOf(buffer, (byte)'\n', start, count - start))
{
    partial.Write(buffer, start, end - start);
    string line = Encoding.UTF8.GetString(partial.GetBuffer(), 0, (int)partial.Length).TrimEnd('\r');
    partial.SetLength(0);
    if (line.Length != 0)
    {
        accept(line);
    }

    start = end + 1;
}
```

`Array.IndexOf` 在本次有效区段里查找换行字节 LF。`(byte)'\n'` 把换行字符常量转换成字节值。找不到时返回 -1，结束 for。

找到一个换行后，把 start 到 end 之前的字节追加进 partial。此前跨块留下的字节已经在 partial 里，所以这一步凑成完整一行，再一次性 UTF-8 解码。

`partial.GetBuffer()` 取得底层缓冲区，只有从 0 到 Length 的部分是当前有效内容。`TrimEnd('\r')` 去掉结尾 CR，兼容 Windows 的 CRLF。然后清空 partial，为下一行准备。

空字符串被跳过，纯空白行仍是非空字符串，会交给 accept。回调执行完成后把 start 更新为换行后位置，再搜索本块下一条行。

这不是每字节都转字符：必须等完整行收齐后才解码，避免一个 UTF-8 多字节字符恰好被切在两次 Read 之间时分别解码产生错误。

## 10. 没换行的尾部留下，预算耗尽即让出

```csharp
partial.Write(buffer, start, count - start);
if (budget == 0)
{
    return false;
}
```

for 完成后，start 后剩下的字节没有换行，把它们留到 partial。下一次读取同文件时会继续拼接。

概念例：服务端正写 `中文\r\n`，当前块只有“中”的前两个 UTF-8 字节。读取器暂存字节，不输出半个字符；下一块把剩余字节和换行补齐后，一次解码并输出 `中文`。

如果文件末尾长期没有换行，Read 返回零，方法可以返回 true，但 partial 中仍有未交出的半行。true 不是“partial 必须为空”。如果随后滚到新分片，旧半行会在切换时丢弃。

预算恰好耗尽时立即 false，即使最后一个字节恰好处于当前文件尾，也等下一轮再确认。调用方据此避免在尚有积压的情况下过早从某个旧就绪事件推导最终状态。

## 11. 回调和内存边界需要按实际实现理解

每次调用最多读取 256 KiB，但不等于总内存最多 256 KiB。一个始终没换行的超长记录可以跨多轮累积到 partial；`ServerInstance` 的 1000 行保留上限也不约束这条未完成记录。

读取器在解析本块前已经把 offset 加上 count。如果 accept 抛异常，中途没有回退游标，也没有重放队列保证该块剩余行之后一定补交。正常调用方 `AddLine` 自己捕获了规定范围内的 JSON 错误，但不能把读取器描述成任意回调失败都无损重试。

同一实例没有锁，依赖调用方按正常 UI 刷新顺序调用。跨线程同时修改 offset/partial 不属于这段实现承诺的用法。

可对照 [日志检查详解](tests/LogChecks.cs.md) 阅读数字分片排序、UTF-8 半行、截断和积压回放场景。本文核对的是源码的读行流程与边界，没有运行这些检查，也没有实际修改日志文件。
