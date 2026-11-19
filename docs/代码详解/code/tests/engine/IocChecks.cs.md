# IocChecks.cs 代码详解

源码：[code/tests/engine/IocChecks.cs](../../../../../code/tests/engine/IocChecks.cs)

## 职责与组装方式

`RunAsync()` 验证真实 `ServerServices.AddProcessServices` 注册的单例共享、容器隔离、异步释放以及启动日志的外部生命周期。它先创建两份 `GameLog`，再为 Router1、Router2 建两个独立容器；没有使用全局服务定位器。

`ProcessRegistrations(log)` 从输出目录加载一个完整 `ServerConfiguration`，调用正式注册入口，并断言配置服务只注册一次。测试随后覆盖 ZooKeeper 与 ID 选项：ZooKeeper 使用非法端口的测试地址，ID worker 根也为测试值。`CreateProvider` 再注册明确进程身份并覆盖 ID 服务工厂，使用 worker 与编号对应的本地雪花生成器，开启 `ValidateOnBuild`、`ValidateScopes`。因此它复用正式组装逻辑，但避免真正分配 ZooKeeper worker。

## 共享、隔离与释放检查

同一容器重复获取 `ServerConfiguration`、日志、ID、ZooKeeper 和节点应保持共享，容器取得的 `ServerGroup` 必须与配置持有的 `Group` 是同一引用；两个容器中的配置、日志、ID、ZooKeeper、节点和进程身份应互相独立。初始化两个 ID 服务后，要求不同 worker 的 ID 不同；两个 Router 节点都可立即就绪。

异步释放第一个容器后，其 ID 不能再用，ZooKeeper 不能再初始化；第二个容器仍可生成合法 ID。对第二个代理执行预取消初始化应返回取消异常，而不是因另一个容器退出而被释放。再次释放第一个容器也不应影响第二个实例。

## 启动日志的所有权

测试暂时把 `Console.Error` 指向 `StringWriter`：第一个容器退出后，外部创建的日志仍须能记录启动错误；另一份缺少已认领身份的注册在解析 `ServerNode` 时失败，容器释放后日志同样必须还能记录错误。解析输出 JSON 的 `Detail` 字段，要求两段错误都存在，确认日志存活时间超过容器失败/退出阶段。

结尾还运行 `ZooKeeperProxyChecks.CheckSessionAsync` 与 `IdGeneratorLifecycleChecks.RunAsync`，进一步检查初始化守卫和服务生命周期。

## 清理与限制

容器使用 `await using`，日志由外层 `using` 最后释放；标准错误流在 `finally` 恢复。这里不进行真实 ZooKeeper 登录或进程占号，容器解析成功也不代表异步初始化已经完成。测试只覆盖当前实际注册及其生命周期，不能替代完整 App 启动检查。
