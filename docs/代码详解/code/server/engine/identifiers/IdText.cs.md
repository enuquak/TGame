# IdText.cs 代码详解

源码：[code/server/engine/identifiers/IdText.cs](../../../../../../code/server/engine/identifiers/IdText.cs)

## 职责与格式规则

`IdText.IsValid(string?)` 是 ID 文本边界校验，接受正的、有符号 64 位范围内的规范十进制整数文本。它先拒绝 `null`、空串和以 `0` 开头的文本，再使用 `NumberStyles.None` 与 `InvariantCulture` 解析为 `long`，最后检查值大于零。

因此 `123` 可以通过，`0`、`00123`、负数、正号、空白、千分位和超出 `long` 范围的文本都会失败。它没有抛出解析异常，而是统一返回布尔值，适合入站检查。

## 使用边界

[LinkService.cs](../LinkService.cs.md) 用它检查元数据里的连接 ID；测试也用它检查生成器输出。它验证的是文本格式，不验证某个 ID 是否由本项目生成、对应哪个 worker、是否已经使用或是否属于当前会话。不会读取时间、访问 ZooKeeper 或保存已见 ID 集合。
