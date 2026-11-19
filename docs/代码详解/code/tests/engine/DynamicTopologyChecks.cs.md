# DynamicTopologyChecks.cs 代码详解

源码：[code/tests/engine/DynamicTopologyChecks.cs](../../../../../code/tests/engine/DynamicTopologyChecks.cs)

## 职责与测试环境

本检查通过 `TestServices`、实际连接配置和 `FrameStream` 验证节点首次达标后在连接增减时保持 `Ready`。`RunAsync(stop)` 不启动网络监听，也不做真实服务发现，而是直接调用 `ServerNode.RunLinkAsync` 注入已指定对端的内存连接。

## 场景顺序

1. 创建没有主动依赖的 Router，立即要求 `FirstLinks` 成功完成且 `Ready` 为真，说明它不等待入站连接。
2. 创建 `Base42`。先接入 `Router1`，要求仍未完成首连，因为还缺 DBMgr 类型；再接入 `DBMgr1`，等待 `FirstLinks` 并要求就绪。
3. 检查 `Gate77` 可以作为入站身份，证明实例编号不受初始启动数量限制；无对应入站关系的 `Scene1` 必须被拒绝。
4. 添加 `Router2`，依次断开 `Router1`、`Router2`。多余连接和失去冗余连接不影响就绪，失去最后一条 Router 连接也保持 `Ready` 为真，已经完成的 `FirstLinks` 保持完成。
5. `WaitUntilReadyAsync` 在尚未重连时即可完成，再接入 `Router9`，验证替代实例可以重新加入。
6. 添加、断开 Gate 入站链路，要求两次操作都不改变 Base 的就绪状态。

## 辅助流程与清理

局部函数 `Open(peer)` 创建流、预写 Hello、分配链路 ID，并保存链路任务。`Close(peer)` 完成输入通道，等待链路退出，保证断线后的断言发生在清理完成之后。整个场景使用关联取消源；`finally` 取消剩余任务并逐一等待，仅忽略由该取消源导致的 `OperationCanceledException`。

## 限制

该测试使用 `TestServices.Startup` 的各类型门槛 `1`，只证明按类型的首次就绪和运行期连接增减行为；首连多实例门槛另由 `StartupReadinessChecks` 覆盖。它没有检验 ZooKeeper 临时占号、地址发布或真实传输重连。
