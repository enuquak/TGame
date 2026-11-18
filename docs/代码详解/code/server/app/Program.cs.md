# Program.cs 代码详解

[查看源码](../../../../../code/server/app/Program.cs)

这是服务器可执行程序的入口。所有进程类型运行同一宿主，唯一命令行参数是 `ProcessType` 名称。角色名称并不触发 DBMgr/Profile 业务初始化；当前只组装进程通信框架。

## 启动主线

1. 检查参数数量，打开 Serilog 自身诊断，再创建 `GameLog`，解析进程类型。日志在配置之前存在，因此加载配置或构建容器失败时仍能输出错误。
2. `RunServerAsync` 根据 `TGAME_SERVER_CONFIG` 构造一个 `YamlConfiguration`，以 `ServerSettings.From` 绑定总配置，再设置最低日志等级。运行目录必须由启动器提前创建，宿主只校验存在。
3. 从同一配置生成只读连接拓扑、数量门槛和 ZooKeeper/ID 选项，先校验再构建 App。此处没有分别读取若干 JSON 文件，也没有热重载。
4. 从根容器取 ZooKeeper，连接后以空地址临时节点占用实例编号；随后 `log.OpenServer` 开启实例日志，并通过临时文件替换发布 PID/启动时间对应的身份记录。
5. 初始化 ID 生成器，再解析节点并启动监听。节点构造依赖的进程身份此时才已确定。
6. 启动停止标记、ZooKeeper 会话和动态连接维护任务。等待 `FirstLinks` 与当前 `Ready`，发布地址后才记录 `PROCESS_READY`。

## WebApplication 和监听

`CreateApplication` 使用精简宿主，清除框架默认日志提供者，注册 gRPC 和项目进程服务。Gate 还注册开发证书：证书由容器创建并释放，启动失败不会留下无人管理的证书对象。

`InitializeApplicationAsync` 在回环地址随机端口监听。Gate 使用 HTTPS/HTTP1；其余角色使用 HTTP2 并映射 `LinkService`。`FindDevelopmentCertificate` 从当前用户证书存储筛选带私钥、有效且具有 ASP.NET 开发证书 OID 的证书，优先选择较晚过期的证书；没有候选时明确失败。

## 运行监督与退出

`WatchStopAsync` 轮询整组、实例名和精确生命周期三类停止标记。`WatchZooKeeperSessionAsync` 在共享会话失效时抛错。`SuperviseAsync` 将任一后台故障转成整体取消，让其他工作一起结束；由正常停止引起的取消不会被当作新的故障。

退出先取消并等待后台任务，再停止监听，随后 `await using` 释放 App 容器，最终外层 `using` 释放日志。顶层 catch 记录 `PROCESS_FAILED` 并设置退出码。参数个数和进程类型解析位于 try 之前，不能把这一部分误认为已经受同一个结构化错误处理器覆盖。

## 阅读关联

配合 `ServerServices.cs` 查看单例注册，配合 `ServerNode` 理解首次连接与持续健康的区别，配合 `ServerRunFiles` 理解身份记录和停止文件。配置加载完成日志仅代表配置校验通过，不代表进程已经可发现或业务已可用。
