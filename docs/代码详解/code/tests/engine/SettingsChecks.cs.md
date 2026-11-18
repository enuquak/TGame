# SettingsChecks.cs 代码详解

源码：[code/tests/engine/SettingsChecks.cs](../../../../../code/tests/engine/SettingsChecks.cs)

## 检查目标

`Run()` 用临时 YAML 文件验证宿主 `ServerSettings` 的字段读取及按需存储校验。测试特意使用 staging、`zk-a`、`mongo-b`、`redis-a` 等值，防止加载器把用户选择的配置替换为固定测试环境默认值。它只解析配置，不连接这些地址，也不读取对应密码变量的内容。

## 场景与断言

`core` 字符串提供环境、运行目录、ZooKeeper endpoint、发现根、worker 根和会话/连接超时；`storage` 提供嵌套 MongoDB、Redis 及密码变量名。两段组合后先调用 `ServerSettings.Load`，再调用 `ValidateProfileStorage`，检查数据库名、用户名、密码变量、缓存前缀、存储地址和发现地址等属性保持输入值。

之后仅写入 `core` 再加载，要求进程基础框架仍可得到 ZooKeeper 设置，说明业务存储配置不是基础进程启动的必填项。但在这份配置上显式调用 `ValidateProfileStorage` 必须抛 `InvalidDataException`，把完整性检查放在真正使用存储的入口。

最后依次写入空文件、只有旧式单项字段的内容，以及将发现根改成相对路径的配置，要求 `Load` 拒绝无效输入。

## 清理与覆盖边界

临时路径使用系统临时目录和 GUID，`finally` 删除文件，无需外部服务。失败通过抛异常交给控制台检查入口处理，本方法本身没有 `PASS` 输出。

本测试没有逐一断言所有已写字段，也不验证真实认证、目录权限或环境变量覆盖；它聚焦“配置值被正确读取”和“可选业务配置按使用场景校验”，不能当作存储连接检查。
