# ProcessType.cs 代码详解

源码：[code/server/engine/configuration/ProcessType.cs](../../../../../../code/server/engine/configuration/ProcessType.cs)

## 职责与类型

`TGame.ProcessType` 是服务器进程角色的唯一枚举定义，包含 `Gate`、`Login`、`Base`、`Scene`、`Router`、`DBMgr`、`Profile`、`GM`。它描述进程类型，不代表某一个正在运行的实例。`Router1` 的类型是 `Router`，实例编号是 `1`。

## 数据如何流动

配置中的角色名称经 `ServerProcess.ParseType` 转为枚举，随后用于连接关系查询、启动数量查询、ZooKeeper 身份认领以及进程行为选择。写回角色名称时使用枚举的规范拼写，`DBMgr` 与 `GM` 的大小写也属于约定。各调用方直接引用枚举，避免维护第二套字符串标识。

枚举没有显式赋值，底层整数按声明顺序从零递增；当前进程名称和 YAML 配置使用名称，不应把这个排列顺序误作稳定的网络协议编号。

## 扩展边界

增加角色不只是多加一个成员：[ServerGroup.cs](ServerGroup.cs.md) 要求每个枚举值都有数量配置，[ServerConfiguration.cs](ServerConfiguration.cs.md) 要求每个枚举值都有主动连接声明。因此缺少配套配置会在加载时失败，不会自动得到默认数量或默认依赖。本文件不创建进程、不注册业务实现，也没有生命周期资源。
