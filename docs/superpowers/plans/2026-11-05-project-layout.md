# 项目目录结构落地计划

> **For agentic workers:** 使用 `superpowers:executing-plans` 在本会话逐项执行。用户已授权在当前项目创建已确认结构，直接落实，不再次请求设计确认。

**Goal:** 创建已确认的目录骨架，迁移现有脚本和协作规范，保持工具入口可用。

**Architecture:** 游戏源码与协作资料集中到 `code/`，项目工具集中到 `tools/`。迁移时同步修正根目录定位、文档链接和忽略规则，不创建尚未设计的 C# 工程。

**Tech Stack:** Windows、BAT、PowerShell 7、Git、Markdown。

**Spec:** [项目文件结构](../../项目文件结构.md)。

## Global Constraints

- 单仓库；保持 `client` 与 `server` 对称；后续结构变动只汇报 diff。
- BAT 入口调用 PowerShell 7；所有项目 PowerShell 脚本集中到 `tools/scripts/powershell/`。
- 不覆盖已有文件，不读取凭据，不中断现有用户级 OpenViking 服务。
- 生成代码与日志不提交；编译产物使用 `build/`；可维护的空目录用 `.gitkeep` 保留。
- 当前仓库文件均未跟踪，HEAD 没有文件；直接在用户指定目录实施，不把既有文件提交成迁移基线。

## Review Focus

- 多一层目录后，隧道状态仍读写仓库根目录 `.local/`。
- BAT 从 Windows PowerShell 5.1 调用时，参数与成功、失败退出码仍正确。
- 仓库根目录与 `code/` 的 Agent 入口均指向迁移后的规范；链接均可解析。
- 三个 `generated/` 和 `logs/` 被 Git 忽略，`data/` 与工具源码未被误忽略。
- 既有脚本与项目内第三方工具迁移后内容保留，活跃文档不再引用旧入口。

## Tasks

- [x] 保存本次涉及文件的迁移前副本与哈希，确认原隧道状态回归检查通过。
- [x] 创建 `code/`、`tools/`、`design/`、`assets/`、`build/`、`logs/`，迁移 `scripts/`、`agents/` 和现有 `.local/tools/crane/`；非生成空目录加入 `.gitkeep`。
- [x] 修正隧道状态根路径，将后续隧道日志写入 `logs/tools/environment-tunnel/`；同步 Agent 入口、文档、忽略规则与 Serena 的源码索引范围。
- [x] 验证目录完整性、Git 忽略、链接、PowerShell 语法、BAT 成功/失败退出码及原有隧道回归检查。
- [x] 按 `ponytail-review` 审查实际改动，更新结构文档和本计划的执行记录。

## 验证命令

- `pwsh -NoProfile -NonInteractive -File tools/scripts/powershell/Test-EnvironmentTunnel.ps1`：状态查询及 BAT 调用退出 0。
- BAT 的 `-Action Invalid`：退出非 0；`Connect-Server.bat -Check`：实际 SSH 检查退出 0。
- `git check-ignore --no-index`：生成代码、日志和构建产物命中；维护的源码与数据未命中。
- 对迁移后 Markdown 链接逐一解析本地目标；对 PowerShell 文件执行语法解析。

## 执行记录

- 原有 `Test-EnvironmentTunnel.ps1` 通过；迁移前隧道未运行，用户级 OpenViking 就绪。
- 尚无 `.csproj` 或解决方案，本次不运行游戏编译，也不安装新的框架或工具。
- 迁移前副本与哈希保存在被忽略的 `.local/project-layout-before-20261105/`。当前项目原有文件均未跟踪，未暂存或提交这些文件。
- 路径回归先复现失败：旧实现从迁移后的路径运行时漏读仓库根目录状态；修正后，状态查询、BAT 调用、失败退出码与迁移状态定位共 4 项检查通过。
- 从 `code/` 经 Windows PowerShell 5.1 调用 BAT：SSH 实际检查退出 0，无效参数退出 1；OpenViking 状态退出 0，无效参数退出 1；隧道状态退出 0。
- 31 个目录、11 项忽略与可跟踪检查、5 个未变脚本及 5 个 crane 文件哈希、4 个 PowerShell 语法检查、34 个 Markdown 本地链接均通过。
- 独立只读审查未发现需要修复的问题。既有服务启停及并发策略不在此次调整范围，保持原行为；本次未启停服务，隧道新日志路径尚未经过真实启动落盘检查。
- 源码目录已准备好；当前聊天仍位于仓库根目录，下一次日常源码开发可打开 `code/`。
