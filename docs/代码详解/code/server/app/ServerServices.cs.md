# ServerServices.cs 代码详解

[查看源码](../../../../../code/server/app/ServerServices.cs)

`ServerServices.AddProcessServices` 是进程服务注册的集中入口，接受宿主已准备好的配置和日志，把它们装进一个 .NET IoC 根容器。静态方法只负责注册，不保存静态服务实例。

## 两类单例注册

`YamlConfiguration`、`ServerSettings`、`ServerConfiguration`、`ServerGroup`、`ZooKeeperOptions`、`IdGeneratorOptions` 都通过 `AddSingleton(existingObject)` 注册。它们来源于一次配置加载：原始快照保留在加载器中，宿主配置用于绑定，拓扑/数量对象负责业务规则校验与只读查询。不同容器可使用不同实例，不是全局单例。

`ZooKeeperProxy`、`IdGenerator` 和 `ServerNode` 使用类型注册，首次解析时由容器调用构造函数，依赖也由容器提供。创建服务不等于已经完成异步初始化，实际顺序由 Program 控制。

## 延迟取得进程身份

`ServerProcess` 使用工厂读取 `ZooKeeperProxy.Process`。宿主必须先初始化 ZooKeeper 并认领身份，然后才能解析节点及其依赖的 `ServerProcess`。这一约束避免在占号前构造一个假的或临时的实例身份。

## 资源所有权

日志也是已有实例注册，但它由 Program 在配置加载前创建，并在容器及错误记录结束后最后释放。容器创建的代理、生成器和节点则由容器统一管理；业务调用方不得擅自释放注入的共享服务。

`IocChecks` 直接调用这个真实注册入口，验证同容器引用相同、不同容器相互隔离和异步释放；因此测试覆盖的是生产组装关系，而不是另一套测试专用注册表。
