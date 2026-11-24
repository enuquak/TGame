# ProcessType.cs 代码详解

源码：[code/server/engine/configuration/ProcessType.cs](../../../../../../code/server/engine/configuration/ProcessType.cs)

这个枚举定义服务器认识的进程类型。它规定身份中的“类型”部分；进程实例的编号、启动数量、主动连接关系分别由其他类型和配置负责。

## 1. 逐项读懂声明

```csharp
namespace TGame;

public enum ProcessType
{
    Gate,
    Login,
    Base,
    Scene,
    Router,
    DBMgr,
    Profile,
    GM
}
```

`public` 允许其他程序集引用；`enum` 定义枚举类型。调用方使用 `ProcessType.Router`，而不是任意拼写的 `"Router"`。

没有显式指定编号，底层类型默认为 `int`，成员从 `Gate = 0` 到 `GM = 7` 依次递增。当前业务代码应使用具名成员，不应把整数顺序当成独立配置或协议约定。

`DBMgr` 和 `GM` 保留项目约定的缩写大小写。名称大小写在后面的严格解析中有意义；`Router` 与 `router` 不是本项目接受的同一个配置拼写。

命名空间是 `TGame`，但源码和类型归属 Engine。业务 Shared 不拥有这组引擎进程身份声明。

## 2. 类型、实例与 PID 是三件事

实际的实例身份声明位于 [ServerGroup.cs](ServerGroup.cs.md)：

```csharp
public sealed record ServerProcess(ProcessType Type, int ChannelId)
{
    public string Role => Type.ToString();
    public string Name => FormatName(Role, ChannelId);
```

`Type` 保存本文件定义的枚举；`ChannelId` 保存从 1 开始的实例编号。`Role`、`Name` 是表达式属性，每次读取时由已有身份推导，不再保存一份可能不同步的字符串。

下面为使用示意：

```csharp
var process = new ServerProcess(ProcessType.Router, 1);
string role = process.Role; // "Router"
string name = process.Name; // "Router1"
```

`Router1` 的 `1` 是实例编号，不是 `ProcessType.Router` 的底层枚举值，也不是操作系统 PID。首个或唯一实例也有编号，不使用无编号的 `Router` 代替实例名。

实例名由 `ServerProcess.FormatName` 统一生成。调用方不应到处用字符串拼接另造一套规则。

## 3. 字符串输入为何还要严格解析

配置与命令行提供的是文本。`ServerProcess.ParseType` 的核心条件是：

```csharp
if (!Enum.TryParse<ProcessType>(role, out var type) || !Enum.IsDefined(type) || type.ToString() != role)
{
    throw new InvalidDataException($"Unknown process type {role}.");
}
```

这里 `TryParse<T>` 是泛型方法，`<ProcessType>` 指明目标枚举类型；`out var type` 让方法把解析结果写到局部变量中。返回的布尔值表示是否解析成功。

`||` 是短路或：前一项为真，后面无需再算。整个判断逐步拒绝解析失败、枚举中没有的数值，以及不能原样还原为规范名称的输入。

最后一项尤其重要。枚举解析可能识别数字文字，但本项目要求外部标识使用规范类型名，例如不能用 `"4"` 代替 `"Router"`。`Enum.IsDefined` 与名称回查分别守住不同边界。

这段代码来自 `ServerGroup.cs`；本文件的枚举声明本身不主动解析命令行或 YAML。

## 4. 声明一种类型，不等于启动一种业务

`process.yaml` 的 `processes` 指定初始启动数量，也作为该类型的首次连接门槛；`connections` 指定主动拨号关系。`ServerConfiguration` 读取并验证连接拓扑，`ServerGroup.GetStartupMinimum` 提供数量。

所以仅在枚举里看见 `DBMgr` 或 `Profile`，不能推导 App 会初始化对应存储和业务服务。当前 App 统一启动进程基础框架，不在启动时初始化 DBMgr/Profile 业务，也不创建实体。

同样，新增枚举成员并不会自动补齐配置、连接规则或实现。枚举只回答“哪些类型名属于这套身份系统”。相关配置详解见 [ServerConfiguration.cs](ServerConfiguration.cs.md) 和 [process.yaml](../../../../../../code/server/config/process.yaml)。

## 5. 生命周期与使用边界

枚举是值类型，没有独立连接、后台任务或释放方法。多个进程可以有相同的 `ProcessType`，但具体 `ServerProcess.Name` 不同。

IoC 根容器共享的是认领完成的 `ServerProcess` 身份对象及服务，不是“每个枚举值自动对应一个全局单例”。理解这个区别，才能正确阅读日志中的 `role`/`instance` 和发现快照中的进程身份。
