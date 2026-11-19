# ServerInstance.cs 代码详解

源码：[ServerInstance.cs](../../../../tools/server-console/ServerInstance.cs)

## 职责与身份模型

ServerInstance 表示控制台的一个启动槽位，并负责追踪实际启动出来的进程。ServerStatus 集中定义中文显示状态，从未启动、初始化中到就绪、连接不全、停止中和异常退出。

SlotName 在构造时由 ServerProcess.FormatName 固定生成，决定 apphost 名称、槽位 JSON 和 starting 标记。Name 最初等于 SlotName，随后可被服务器认领到的身份替换，例如 Router1 槽位实际成为 Router7。Role 始终是进程类型；Pid 与 startedUtcTicks 共同标识一次进程生命期，不能只凭可复用的 PID 或实例编号停止进程。

Identity 记录保存 PID 和 UTC 启动 ticks。process 是附加的系统进程句柄；logRoot、logDir 与 RollingLog 确定日志来源。initializing、initialized、connected、failed 是从有序日志事件重放得到的历史状态，并非重复存储当前显示字符串。

## 打开与恢复

Open 先 Reset，设置运行目录、日志根和快照路径，读取槽位 JSON，并检查 starting-<槽位> 标记。RestoreProcess 按槽位 apphost 的进程名枚举候选项；正常恢复要求 PID、启动时间和完整可执行路径都匹配。有 pending 标记时允许恢复已经启动但尚未存好 PID 的进程，仍要求 apphost 路径属于本快照。

恢复发现第二个匹配进程会抛 InvalidDataException，避免任意选择。候选进程在检查中退出属于正常竞态；未被采用的 Process 句柄在 finally 释放。恢复后的身份通过临时文件与移动写回，pending 标记随后删除。

Launch 先写启动标记，再清理旧句柄和日志状态，调用 Process.Start，保存 PID/时间及日志读取器，最后删除启动标记。这一顺序使界面在 Start 与保存 PID 之间退出时仍有恢复线索。

## 身份与日志驱动的状态

Refresh 无 PID 时直接返回。身份尚未解析时，读取由 ProcessKey(PID, ticks) 定位的 identity 文件；通过 ServerProcess.Parse 校验名称并检查类型一致，随后切换 Name 和日志目录。它不会用新的真实编号覆盖稳定槽位。

RollingLog.ReadAvailable 把完整记录交给 AddLine。只对日志滚动造成的目录消失和共享/锁冲突延迟重试；其他 I/O 异常继续上抛。内存保留最近 1000 行，而 LineCount 持续累计，供 UI 使用绝对行号定位。

AddLine 验证 role、pid、可选 instance 和时间戳，排除其他进程及早于本生命期的记录。合法事件驱动 ApplyEvent：LISTEN 表示初始化开始，PROCESS_READY 表示完成初始化，LINKS_READY/LOST 更新连接状态，PROCESS_FAILED 设置不可被后续连接事件清除的失败状态。损坏记录仍加入可见日志，但不能改变进程状态。

状态优先级是失败、已退出、停止标记、回放未追平、初始化已完成、其他启动态。回放没完成时即使读到历史 READY 也显示初始化中；若日志出现 LINKS_LOST，已初始化的进程显示连接不全，后续 LINKS_READY 可恢复显示。

上述 LINKS_LOST 分支用于既有日志事件的解析。当前 ServerNode 首次达标只记录一次 LINKS_READY，运行期依赖断线不再产生 LINKS_LOST，因此普通对端断线不会让当前进程退出就绪显示。不能把控制台的日志重放状态当成实时连接数量；进程自身退出、停止或失败仍按前面的优先级更新。

## 关闭与资源释放

StopAsync 只处理仍活着的当前 Process。它写 stop-process-<ProcessKey> 标记，让服务器按精确生命期自行退出，等待 10 秒后才强制 Kill，并再等待最多 5 秒。竞态中已经退出的进程不会当成 Kill 错误；结束后 Refresh 更新状态。

Reset 释放旧句柄并重置日志、身份和历史状态。Dispose 本身仅释放 Process 包装对象，不 Kill 进程，这支持关闭控制台后重新打开并附加。日志状态、磁盘标记和系统生命期信息共同决定显示，任何单一来源都不足以证明进程属于当前槽位。
