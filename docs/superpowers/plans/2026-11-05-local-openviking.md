# 本机 OpenViking 配置与隧道启动修复

> **For agentic workers:** 使用已确认的本机部署方案逐项执行和验证。用户已授权直接配置及复现隧道问题，继续在本会话执行。

**Goal:** 在 Windows 本机配置 OpenViking，并修复隧道脚本通过 Windows 默认入口运行时的问题。

**Architecture:** OpenViking 仅监听本机 `127.0.0.1:1933`，记忆数据保存在用户目录；内容处理使用现有 Codex 授权，Embedding 使用本地小模型。云端 7 个基础服务继续通过现有 SSH 隧道访问。

**Tech Stack:** BAT 入口 + PowerShell 7 实现、uv/Python 3.13、OpenViking 0.5.3、官方 CPU llama-cpp-python wheel、Codex 插件/MCP。

**Spec:** 本会话用户已确认“先进行本机配置”；项目连接标准见 `AGENTS.md`，原环境清单见 `docs/公众号/技术方案的选型/环境配置.md`。

## Global Constraints

- 保留 `TGame-server`、严格主机密钥校验和回环端口绑定。
- 本机模型授权和密码不写入仓库、日志或聊天。
- 优先使用官方 Windows 预编译包，固定实际安装版本。
- 保留其他已有工具和 Codex 配置；测试不读取或导入无关会话。

## Review Focus

- BAT 可从 Windows 默认 PowerShell 5.1 与 PowerShell 7 启动，内部始终使用 PowerShell 7。
- 双击入口能保留失败信息；真实 Start/Stop 能管理同一个隧道。
- OpenViking 本机依赖、模型授权和 Embedding 均须实际调用验收。
- 知识写入、检索和进程重启后的保留均须验证。
- MCP 可用不等于自动记忆 hooks 已被用户信任；分别记录结果。

## Tasks

- [x] 复现默认 PowerShell 版本不匹配，先建立失败回归检查，再修正版本要求。
- [x] 按用户最终决定，使用保留窗口输出的 `.bat` 入口，集中 `.ps1` 到 `tools/scripts/powershell/`。
- [x] 安装固定版本 OpenViking 与本地推理依赖，检查 CLI/本机引擎加载。
- [x] 配置回环服务、受限凭据目录、现有 Codex 授权及本地 Embedding，运行 doctor。
- [x] 启动服务，完成知识写入、检索、重启保留和 MCP 接入检查。
- [x] 同步项目文档中的本机部署、端口、启动和故障排查说明，记录剩余限制。
- [x] 用户授权后通过原生 `/hooks` 保存信任；实测自动采集 2 条消息、提交 1 次、生成 1 条长期记忆，并在全新会话自动召回。

## 执行记录

- 隧道根因：原脚本要求 PowerShell 7，却由 Windows 默认 5.1 启动，以 `ScriptRequiresUnmatchedPSVersion` 失败。最终按用户决定采用 BAT 显式选择 PowerShell 7，保留窗口输出；5.1 仅作为 BAT 的调用终端，不再作为脚本运行时。
- Embedding 依赖：PyPI 没有可用的 llama-cpp-python wheel，改用上游文档指定的官方 CPU wheel 源；没有关闭 TLS 验证或启用源码编译。
