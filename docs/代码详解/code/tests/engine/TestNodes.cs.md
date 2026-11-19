# TestNodes.cs 代码详解

源码：[code/tests/engine/TestNodes.cs](../../../../../code/tests/engine/TestNodes.cs)

## 职责与入口

`TestNodes` 集中两个很小的测试辅助操作。

`Hello(peer)` 创建选择 `Hello` 分支的引擎 `Frame`，把传入实例名作为握手身份。它不校验名称、不注册身份，也不完成握手；测试把帧写入 `FrameStream.Input`，实际的身份和连接规则由 `ServerNode` 或 `PeerLink` 检查。

`LoadConfiguration()` 从 `AppContext.BaseDirectory/config/` 拼接 `ServerConfiguration.FileName`，调用 `ServerConfiguration.Load` 取得完整配置及其连接拓扑。这样测试读取的是构建输出随附的配置，不依赖启动命令所在的当前目录。

## 执行条件与边界

运行测试前需要把完整配置入口及其 include 文件复制到输出目录。文件缺失、格式错误或拓扑不合法会由配置加载器抛出，辅助方法不捕获、不提供默认配置。返回的是独立配置实例，不是全局可变状态；本文件不创建可释放资源，也不表示测试进程已经连接到真实节点。
