# ServerRuntime.cs 代码详解

源码：[code/server/engine/configuration/ServerRuntime.cs](../../../../../../code/server/engine/configuration/ServerRuntime.cs)

## 职责

`ServerRuntime` 是运行目录配置的薄入口，没有全局可变状态。`FileName` 和 `ConfigurationPath` 都引用 [ServerConfiguration.cs](ServerConfiguration.cs.md)，因此运行目录仍属于统一的 `server.yaml`，不是独立格式或独立配置源。

## 两个操作

`LoadDirectory(path, workingDirectory)` 加载并校验完整 `ServerConfiguration`，再调用 `ResolveRunDirectory`：读取 `runDirectory`，要求其为非空相对路径，最终按传入的工作目录解析成绝对路径。相对路径的基准是 `workingDirectory`，不是 YAML 文件所在目录。

`Save(path, directory, workingDirectory)` 先用 `Path.GetRelativePath` 将目标运行目录转换为相对工作目录的路径，再从当前配置生成快照，覆盖写入同一路径并更新 `runDirectory`。include 内容会随快照展开保存，其他配置节保留。

## 边界

保存要求输入配置文件已经存在且是有效的完整服务器配置；它不是空白配置生成器。不同盘符等情况可能使计算结果仍是绝对路径，随后会被 `ValidateRunDirectory` 拒绝。该类型不创建实际运行目录，也不启动或停止进程。文件读写和路径错误直接交给调用方处理。
