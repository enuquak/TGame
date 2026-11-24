# ZooKeeperOptions.cs 代码详解

源码：[code/server/engine/ZooKeeperOptions.cs](../../../../../code/server/engine/ZooKeeperOptions.cs)

这个类型保存连接 ZooKeeper 所需的配置，并提供本地校验。它本身不会连接服务，也不会创建临时节点。配置从 [ops.yaml](../config/ops.yaml.md) 经 `ServerConfiguration` 加载后，由 App 注册到根容器，再交给 `ZooKeeperProxy`。

## 1. sealed record 和 init 表达什么

```csharp
public sealed record ZooKeeperOptions
```

`public` 表示其他程序集可以使用；`sealed` 禁止派生；`record` 表示这是一个以数据为主的类型，编译器会提供值比较和 `with` 复制等支持。

字段声明为属性，例如：

```csharp
public string Endpoint { get; init; } = "";
public string DiscoveryRoot { get; init; } = "";
public string WorkerRootPath { get; init; } = "";
```

`get` 允许读取，`init` 允许在对象初始化阶段赋值。调用方不能在对象已经创建后像普通 `set` 属性一样任意改写。右边的空字符串是初始值，不表示这个配置可以省略；后续校验会拒绝缺少的必要内容。

测试中的 `options with { Endpoint = "127.0.0.1:invalid-port" }` 会产生一份修改了 Endpoint 的副本，原 options 的属性值不变。`with` 是浅复制，不会自动深拷贝所有引用成员；本类型的主要成员是字符串和整数，理解成复制一组配置值即可。

`init` 也不等于自动热重载。当前程序启动时加载并注册配置对象，之后读取这份对象，不会因为磁盘 YAML 被编辑而自动变化。

## 2. 两个常量指出配置节和总入口

```csharp
public const string SectionName = "zooKeeperOptions";
public const string FileName = ServerConfiguration.FileName;
```

`SectionName` 是配置节名字，和 YAML 的 `zooKeeperOptions:` 对应。`FileName` 引用统一入口的文件名常量，当前是 `server.yaml`；它不是说 ZooKeeper 字段必须直接写在这个文件内。

实际结构是总入口通过 `include: [ops, process]` 加载同目录文件，再合并顶层配置。ZooKeeper 字段目前放在 `ops.yaml`，使用方仍从总入口加载。复用已有常量，可以避免多个类型维护不同的入口名字。

## 3. 五项配置分别影响什么

| 属性 | 当前示例值 | 使用位置与意义 |
| --- | --- | --- |
| `Endpoint` | `127.0.0.1:2181` | 交给实际 ZooKeeper 客户端建立连接 |
| `DiscoveryRoot` | `/tgame/test` | 进程身份与地址记录的父路径 |
| `WorkerRootPath` | `/tgame/id-workers` | 雪花 worker 编号的占用路径，进一步交给 ID 配置 |
| `SessionTimeoutMilliseconds` | `10000` | 创建客户端时请求的会话超时，单位毫秒 |
| `ConnectTimeoutMilliseconds` | `10000` | 本地等待连接、申请 worker 和等待删除等操作使用的期限 |

发现根下的节点可以是 `/tgame/test/server-Router1`。worker 根下可以是 `/tgame/id-workers/0`。两种编号各自分配，不应把 `Router1` 中的 1 自动当作 worker 编号。

会话超时描述服务端如何判定会话失活；本地操作期限描述当前调用方愿意等多久。两个值碰巧都是 10000，不代表它们是同一项规则。ZooKeeper 会协商实际会话超时；本项目又在已连接后观察到 `Disconnected` 时提前永久失效，因此不是一定等满这个会话超时才停止发号。

超时属性写为：

```csharp
public required int SessionTimeoutMilliseconds { get; init; }
public required int ConnectTimeoutMilliseconds { get; init; }
```

`required` 要求普通 C# 对象初始化时提供这些成员。它不会自动判断数值是否合理，填入 0 仍然需要 `Validate()` 拒绝。外部数据绑定也不能只依靠编译期 required 来证明内容合法。

## 4. Load 实际加载完整配置

```csharp
public static ZooKeeperOptions Load(string path)
{
    return new ServerConfiguration(path).ZooKeeperOptions;
}
```

先创建 `ServerConfiguration`，由它解析总入口及 include、绑定并校验配置，然后取其中的 `ZooKeeperOptions`。这不是直接解析一小段 YAML 的捷径，也不能把只有 ZooKeeper 字段的任意片段文件当成完整总入口。

在正常 App 启动路径中，已经有一个统一的 `ServerConfiguration`，`ServerServices` 注册的是它持有的选项实例。无需为了给每个服务获取配置反复调用这个静态 Load。这个方法便于需要自己建立配置对象的独立调用方使用。

相关加载过程见 [ServerConfiguration.cs](configuration/ServerConfiguration.cs.md)，注册关系见 [ServerServices.cs](../app/ServerServices.cs.md)。

## 5. Validate 做了哪些检查

```csharp
public void Validate()
{
    ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint);
    ValidatePath(DiscoveryRoot);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SessionTimeoutMilliseconds);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConnectTimeoutMilliseconds);
}
```

第一句拒绝空地址，第二句检查发现根路径，后两句要求超时为正数。任何一步失败都会抛异常，调用方不能继续把这组值当作有效配置。

注意检查的实际边界：这里没有解析 Endpoint 的端口格式，也没有尝试连接地址。因此 `127.0.0.1:invalid-port` 可以通过“非空”这一层，但会在创建真正客户端时失败。`ZooKeeperProxyChecks` 正是利用这点检查初始化路径。

这个方法也没有直接调用 `ValidatePath(WorkerRootPath)`。统一配置加载时会把该字段放入 `IdGeneratorOptions`，再由 `Identifiers.Validate()` 校验。worker 申请方法本身还会再次验证传入根路径。因此不能把单独调用 `ZooKeeperOptions.Validate()` 描述成已经校验了所有五项内容。

## 6. ValidatePath 逐步拒绝不合法的路径结构

```csharp
ArgumentException.ThrowIfNullOrWhiteSpace(path);
if (!path.StartsWith('/') || path.EndsWith('/'))
{
    throw new ArgumentException("ZooKeeper path must be absolute and have no trailing slash.");
}
```

先拒绝空值，再要求以 `/` 开头且不能以 `/` 结尾。`||` 表示任一违规就失败。

这意味着本方法也不接受只有一个 `/` 的根路径，因为它以斜杠结尾。项目需要的是具体的命名空间父目录，例如 `/tgame/test`。

接下来检查路径段：

```csharp
foreach (var segment in path[1..].Split('/'))
{
    if (segment.Length == 0 || segment is "." or "..")
    {
        throw new ArgumentException("Invalid ZooKeeper path segment.");
    }
}
```

`path[1..]` 是范围索引，取从索引 1 到末尾的字符串，也就是去掉已确认存在的开头 `/`。`Split('/')` 把剩余路径按斜杠切成段，`foreach` 逐段检查。

`segment is "." or ".."` 是模式匹配，表示当前段是 `.` 或 `..`。本方法不对它们做相对路径归一化，而是明确拒绝。

| 输入 | 结果与原因 |
| --- | --- |
| `/tgame/test` | 通过本地结构检查 |
| `tgame/test` | 没有起始 `/` |
| `/tgame/test/` | 末尾有 `/` |
| `/tgame//test` | 中间出现空段 |
| `/tgame/../test` | 有 `..` 段 |
| `/` | 末尾有 `/`，不是项目所需的命名空间路径 |

这里展示的是代码显式检查的结构规则，不等于穷尽 ZooKeeper 对所有字符的要求。进一步的库校验和服务端错误仍可能在实际操作时出现。

## 7. 把校验与运行串起来

```text
server.yaml include ops.yaml
    → ServerConfiguration 绑定 ZooKeeperOptions
    → 创建 IdGeneratorOptions 并分别 Validate
    → App 将选项对象注册为单例
    → ZooKeeperProxy 构造时再次检查必要配置
    → InitializeAsync 才真正创建客户端、等待会话可用
```

配置校验成功仅说明已通过对应的本地检查，不等于网络可达、认证成功或 worker 编号已经申请到。后续运行状态与清理见 [ZooKeeperProxy.cs](ZooKeeperProxy.cs.md)，本地失败与会话行为的测试见 [ZooKeeperProxyChecks.cs](../../tests/engine/ZooKeeperProxyChecks.cs.md)。
