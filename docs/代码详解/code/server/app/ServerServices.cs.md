# ServerServices.cs 代码详解

[查看源码](../../../../../code/server/app/ServerServices.cs)

`ServerServices.AddProcessServices` 是进程服务注册的集中入口，接受宿主已准备好的配置和日志，把它们装进一个 .NET IoC 根容器。静态方法只负责注册，不保存静态服务实例。

## 两类单例注册

`ServerConfiguration` 只注册一次，`ServerGroup`、`ZooKeeperOptions`、`IdGeneratorOptions` 分别直接注册配置持有的 `Group`、`ZooKeeperOptions`、`Identifiers`，都使用 `AddSingleton(existingObject)`。同一配置对象完成解析、值持有和拓扑查询，注册入口不重新绑定或创建平行配置。不同容器可使用不同配置实例，不是全局单例。

`ZooKeeperProxy`、`IdGenerator` 和 `ServerNode` 使用类型注册，首次解析时由容器调用构造函数，依赖也由容器提供。创建服务不等于已经完成异步初始化，实际顺序由 Program 控制。

## 延迟取得进程身份

`ServerProcess` 使用工厂读取 `ZooKeeperProxy.Process`。宿主必须先初始化 ZooKeeper 并认领身份，然后才能解析节点及其依赖的 `ServerProcess`。这一约束避免在占号前构造一个假的或临时的实例身份。

## 资源所有权

日志也是已有实例注册，但它由 Program 在配置加载前创建，并在容器及错误记录结束后最后释放。容器创建的代理、生成器和节点则由容器统一管理；业务调用方不得擅自释放注入的共享服务。

`IocChecks` 直接调用这个真实注册入口，验证同容器引用相同、不同容器相互隔离和异步释放；因此测试覆盖的是生产组装关系，而不是另一套测试专用注册表。
