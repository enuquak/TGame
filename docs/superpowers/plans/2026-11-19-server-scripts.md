# 独立服务器进程脚本 Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan inline. Steps use checkbox syntax for tracking.

**Goal:** 提供不依赖服务器控制台的启动、停止和强退脚本。

**Architecture:** StartAll 按配置数量调用各类型的 PowerShell 入口，每种类型另有 BAT 入口；共用管理逻辑。App 从 runtime.json 读取运行目录，只接收类型参数，目录统一由脚本创建。

**Tech Stack:** PowerShell 7、Windows BAT、.NET 10 测试假进程。

**Spec:** [设计](../specs/2026-11-19-server-scripts-design.md)

## Global Constraints

- 继续使用当前 `review-1118` 分支，保留停止信号协议；App、控制台与相关测试同步移除目录参数。
- BAT 位于 `tools/scripts/`，PowerShell 实现及检查位于 `tools/scripts/powershell/`。
- 复用引擎配置、实例命名与停止标记常量，不新增依赖。
- 验证仅限短时隔离检查，不执行真实进程组联调。

## Review Focus

- 同名但不同完整路径的进程不得被停止或强退。
- 重复启动、并发管理不得产生重复进程或丢失运行组记录。
- 部分进程退出后补起必须复用环境与进程数量快照。
- 正常停止超时必须保留仍存活进程并返回非零退出码。
- 工作目录包含空格、无当前组、非法当前组标记必须有明确行为。

## Task 1: 脚本入口与进程管理

**Files:** 新增 StartAll/Start<Type>/StopAll/KillAll 入口及共用脚本、`Test-ServerScripts.ps1`、`ServerRuntime.cs`、`runtime.json`；修改 App、控制台相关调用点及文档。

**Interfaces:** BAT 转发 `-NoBuild` 或 `-TimeoutSeconds`；共用脚本接受 `-Action StartAll|Start|Stop|Kill`。StartAll 向类型脚本传递当前上下文及槽位，在同一独占锁内完成启动。`ServerRuntime.LoadDirectory(path, workingDirectory)` 解析相对目录；`New-ServerRunDirectory.ps1 -Configuration` 创建目录。正常退出为 0，配置错误、启动失败或停止超时为非零。

- [x] 编写隔离假进程检查，先确认缺少入口导致失败。
- [x] 实现按类型启动、配置路径、快照、独占管理、启动/补起、停止与强退；同步修改 App 与控制台。
- [x] 运行短时检查，验证上述 Review Focus 和 BAT 的成功/失败退出码。
- [x] 更新 README 与项目结构说明，检查 diff，完成代码审查。
- [x] 在当前分支提交本事项文件，不自动合并整个已有分支。

## 执行记录

- 首次检查发现旧 Import-ServerTypes.ps1 导入了依赖隐式 using 的无关 ServerConfiguration，改为显式导入脚本使用的类型，并补齐 ServerRunFiles 的 System using。
- 按用户追加要求，将三入口方案细化为类型入口和 StartAll，目录改由 runtime.json 配置与脚本创建。
- 隔离检查发现带标准输出重定向的后台启动会继承调用者句柄；改用隐藏的 ShellExecute 独立启动，避免命令调用者一直等待输出管道关闭。
- 独立审查发现并修复：旧控制台快照补起须提示编译重启；停止/强退不读取启动数量文件；清理旧构建后仍可创建新运行组。隔离检查覆盖缺失数量记录与缺失快照，控制台配置检查覆盖旧版拒绝与新版单参数。
- 验证通过：Test-ServerScripts.ps1（包含默认构建、BAT 成功/失败退出码、单类型与整组启动、补起、配置路径、停止超时、强退隔离）；App/控制台相关工程编译；控制台 --config-only；PowerShell 语法及 git diff --check。
- 未执行真实 ZooKeeper/服务器组联调或完整测试。Program.cs 中与本任务无关的并行日志初始化顺序调整保留在工作区，不纳入本次提交。
