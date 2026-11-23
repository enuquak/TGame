# IdText.cs 代码详解

源码：[code/server/engine/identifiers/IdText.cs](../../../../../../code/server/engine/identifiers/IdText.cs)

`IdText.IsValid` 在入站连接早期检查连接 ID。`LinkService` 从 gRPC 元数据取出 ID 并交给 `ServerNode.AcceptConnectAsync`，节点调用此方法，返回假时可直接拒绝该连接。占号、就绪以及后续握手由节点另外检查。见 [LinkService.cs](../LinkService.cs.md)、[ServerNode.cs](../ServerNode.cs.md)。

1. `#nullable enable` 开启本文件的可空引用检查。参数写成 `string?`，调用方可以传 `null`。`string.IsNullOrEmpty(value) || value[0] == '0'` 使用短路或，前半边为真时跳过索引访问，因而 `null` 和空串都安全地返回假，首字符为 `0` 的文本也在这一段排除。
2. `long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id)` 用固定的区域规则尝试解析成有符号 64 位整数。`out long id` 把解析结果写到调用处声明的局部变量里，方法本身返回是否成功。`&& id > 0` 在解析成功后才执行正数检查。

例如 `"123"` 和 `"9223372036854775807"` 通过。`"0"`、`"0123"`、带正负号、普通前后空格、千分位或数值超出 `long` 范围的文本会失败。判断上限来自 `long` 的数值范围。实际接收规则沿用 `long.TryParse` 的行为，例如末尾空字符 `'\0'` 可被解析器忽略，此处没有逐字符扫描。生成路径用 `InvariantCulture` 输出普通十进制文本。见 [SnowflakeIdGenerator.cs](SnowflakeIdGenerator.cs.md)、[IdGeneratorChecks.cs](../../../tests/engine/IdGeneratorChecks.cs.md)。
