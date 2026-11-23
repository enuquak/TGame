# ProcessType.cs 代码详解

[源码 ProcessType.cs](../../../../../../code/server/engine/configuration/ProcessType.cs)

## 类型、配置和实例名

`TGame.ProcessType` 声明服务器能够识别的八种进程类型，依声明顺序是 `Gate`、`Login`、`Base`、`Scene`、`Router`、`DBMgr`、`Profile`、`GM`。代码里写 `ProcessType.Router` 得到一个枚举值。它可以用于条件判断或作为字典键。`enum` 省略了底层整数赋值，因此成员从 0 开始递增，当前配置和进程身份按成员名称传递。

例如配置的 `processes: { Router: 2 }` 片段表达 `Router` 类型有两个初始启动槽位。完整配置绑定后，`ServerGroup` 生成 `ServerProcess(ProcessType.Router, 1)` 和 `ServerProcess(ProcessType.Router, 2)`。身份的 `Type` 是枚举，`Role` 由 `Type.ToString()` 得到 `Router`，`Name` 则由 `ServerProcess.FormatName` 得到 `Router1`、`Router2`。实际启动时 App 只接收角色参数，最终实例编号由 ZooKeeper 临时占号确定。相同的数量也是主动连接方第一次等候该类型的门槛。 [进程组与身份](ServerGroup.cs.md) · [启动参数与占号](../../app/Program.cs.md)

## 字符串如何变成枚举

```csharp
Enum.TryParse<ProcessType>(role, out var type)
```

这句出现在同目录的 `ServerGroup.cs`，由 `ServerProcess.ParseType` 使用。`<ProcessType>` 指定希望解析的类型，`out var type` 声明输出变量，方法通过返回的布尔值表示是否解析成功。调用方还用 `Enum.IsDefined(type)` 检查值属于已声明成员，用 `type.ToString() != role` 强制输入与规范名称完全一致。因此 `Router`、`DBMgr` 可以通过，`router`、`DbMgr`、数字文本 `4` 都会因校验失败抛出 `InvalidDataException`。`Router1` 属于实例名称，入口是 `ServerProcess.TryParse` 或 `Parse`，解析时另外检查数字后缀。

当 C# 代码判断类型时，使用 `process.Type == ProcessType.Gate`。需要取编译期名称时，可以写 `nameof(ProcessType.Gate)` 得到字符串 `Gate`。需要读取运行期枚举值的名称时调用 `ToString()`。例如 App 用枚举值判断 Gate 的监听方式，并用 `ToString()` 取得日志的角色字段。

## 添加角色时跟着检查什么

`ServerGroup` 通过 `Enum.GetValues<ProcessType>()` 遍历类型并检查每类都有数量，即使数量为 0。`ServerConfiguration` 解析 `connections` 的起点和目标，也要求每个类型有一条声明。新增枚举成员后，两个配置段都要补齐，例如一个暂不启动且没有主动目标的角色对应数量 `0` 和目标 `[]`。具体拓扑及是否允许建连，仍由合并后配置决定。 [拓扑配置解析](ServerConfiguration.cs.md)
