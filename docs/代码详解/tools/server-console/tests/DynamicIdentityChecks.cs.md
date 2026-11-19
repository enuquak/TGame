# DynamicIdentityChecks.cs 代码详解

源码：[DynamicIdentityChecks.cs](../../../../../tools/server-console/tests/DynamicIdentityChecks.cs)

## 为什么需要真实本地进程

这个轻量检查验证控制台启动槽位与实际认领编号不同的情况。它不启动 TGame.Server，而是把 Windows sort.exe 复制成临时 Router1Server.exe。重定向标准输入使 sort 等待输入，从而提供真实、稳定的进程路径、PID 和启动时间，满足 ServerInstance 的附加校验。

临时目录使用 GUID 隔离，ProcessStartInfo 禁止 ShellExecute 并隐藏窗口。using 负责释放 Process 句柄，finally 保证异常时仍能杀掉测试子进程。

## 构造动态身份

测试读取 child.StartTime 的 UTC ticks，写 Router1.json 保存 PID/时间。随后用 ServerRunFiles.ProcessKey 定位服务器身份文件，把认领结果写成由 ServerProcess.FormatName 产生的 Router7。

日志目录也按 Router7Server 建立，而不是槽位 Router1。日志文件名前缀仍是 PID-ticks，内容包含与真实进程和实例匹配的 LINKS_READY、PROCESS_READY 事件。

## 附加和停止断言

创建 ServerInstance(nameof(ProcessType.Router)) 默认对应 Router1 槽位，调用 Open 后要求 Name 已变成 Router7、Alive 为真、Status 为就绪。这个组合检查同时覆盖进程恢复、动态身份解析、日志目录切换和事件回放。

调用 StopAsync 后立刻检查 stop-process-<PID-ticks> 文件存在。停止请求不能使用可被后续服务器复用的 Router7 名称；它必须指向本次具体生命期。随后关闭 child.StandardInput 让 sort 正常退出，等待 StopAsync 完成。

## 生命周期与覆盖边界

测试自身不模拟服务器消费停止标记，而是直接关闭 stdin。因此它证明控制台写出了正确停止地址、能等到子进程退出，不证明真实宿主的停止轮询实现。后者由集成流程验证。

如果任何断言失败，finally 检查 HasExited，仍存活则 Kill 并等待退出。成功才输出身份、日志和停止协议的 PASS。该检查在 --identity-only 和 --state-only 下均执行，不需要数据库、ZooKeeper 或网络。
