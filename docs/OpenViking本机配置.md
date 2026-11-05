# OpenViking 本机配置

2026-11-05 已在 Windows 本机配置 OpenViking 0.5.3，服务监听 `127.0.0.1:1933`，启用 API key 认证。云服务器不部署 OpenViking，使用时无需 SSH 隧道。

## 日常启动

双击 `tools/scripts/Start-OpenViking.bat`；或在项目根目录执行：

```powershell
.\tools\scripts\Start-OpenViking.bat
.\tools\scripts\Start-OpenViking.bat -Action Status
.\tools\scripts\Start-OpenViking.bat -Action Stop
```

BAT 统一调用 PowerShell 7，并保留执行结果。自动化直接调用 `pwsh -NoProfile -File ./tools/scripts/powershell/Start-OpenViking.ps1`，避免等待按键。首次启动可能需要几分钟，`ready=True` 表示服务就绪。当前没有设置 Windows 开机自启。

停止前等待知识写入完成；Stop 会终止本脚本启动的服务进程及其 Python 子进程，保留磁盘数据，但不是备份操作。

## 模型与配置

| 项目 | 实际配置 |
| --- | --- |
| 内容处理 | `openai-codex`，`gpt-5.6-terra`，复用本机已有 Codex ChatGPT 登录 |
| Embedding | 本机 CPU，`bge-small-zh-v1.5-f16`，512 维 |
| Python / 服务 | uv 管理 Python 3.13，`openviking[local-embed]==0.5.3` |
| Codex 接入 | 官方 `openviking-memory@openviking` 插件 0.11.18，stdio MCP 代理 |
| 插件 Node 运行时 | 使用官方插件的 `node` 命令，由宿主环境解析；独立诊断可使用现有 Node 24.22.1 |
| 客户端身份 | `tgame` 账号下的 `developer`；客户端使用该账号的密钥，与服务根密钥分开 |

可以复用现有 Codex 登录做内容处理，但 OpenViking 仍需要自己的服务和 Embedding；当前 Embedding 在本机运行，内容处理会联网并受账号权限、额度影响。[官方 Codex 接入说明](https://docs.openviking.ai/en/agent-integrations/04-codex)

私有文件位于 `%USERPROFILE%\.openviking`：

- `ov.conf`：服务、存储和模型配置，包含服务根密钥。
- `ovcli.conf`：MCP 和 hooks 的客户端地址、账号密钥及默认行为。
- `data/`：知识和索引数据；需要备份时，在无写入且服务已停止后连同私有配置备份到受限目录。
- `server.stdout.log`、`server.stderr.log`：启动与运行日志。
- `server-process.json`：脚本管理的进程标识。

该目录 ACL 仅允许 Administrator 和 SYSTEM。doctor 的 POSIX 文件权限提示不代表 Windows ACL 已开放。模型缓存位于 `%USERPROFILE%\.cache\openviking\models`；凭据和知识数据不提交 Git。

项目 `.openviking/config.json` 只保存无密钥的工作区策略：TGame 启用采集与召回，并使用 `tgame` peer；其他工作区默认关闭自动采集/召回，默认关闭启动时的全局 profile 注入。

独立 MCP 测试曾在 PATH 原有 Node 24.14.0 的退出阶段出现 `UV_HANDLE_CLOSING` 断言，显式使用现有 `%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe`（24.22.1）后通过。后续确认 Codex 启动会恢复插件缓存，因此不把直接编辑缓存中的 `.mcp.json`、`hooks/hooks.json` 当作持久配置。本次自动记忆验收使用恢复后的官方插件，通过原生 `/hooks` 信任；未升级全局 Node。若独立 stdio 测试遇到上述退出断言，显式使用新版 Node。[Node 上游修复记录](https://github.com/nodejs/node/pull/61999)

## 自动记忆已接入

已通过真实 Codex 模型调用、中文知识导入、本地 Embedding、语义检索、服务 Stop/Start 后数据保留、独立 MCP 客户端的工具发现与检索。验收资源为 `viking://resources/TGame-environment-smoke/TGame-environment-smoke.md`，包含 MongoDB 37017、Redis 16379 等项目环境信息。

2026-11-05 已按用户授权，在 Codex CLI 的原生 `/hooks` 界面信任 OpenViking 的 7 个 hook 命令；界面显示 Active，诊断确认核心 hooks 的信任记录已保存。未使用跳过信任的开关，也未手工写入信任哈希。

已完成真实会话验收：

- 自动采集：测试会话 `01a1a7b8-8df0-7170-9a6d-c1349c1b0235` 采集 2 条消息，在退出时自动提交 1 次，生成 1 条长期记忆。
- 记忆内容：项目既有的 C# 14、.NET 10 CoreCLR/JIT、业务热重载和服务端不使用 Native AOT 的约定。
- 跨会话召回：全新会话 `01a1a7b9-9227-7431-8d91-c01a1b687af5` 未读文件、未调用工具，凭自动注入的记忆正确回答，并引用 `viking://user/developer/peers/tgame/memories/entities/software_project/tgame.md`。
- CLI 两次验收均退出 0，插件 doctor 为 0 个失败项。输出保存在被 Git 忽略的 `.local/openviking-hooks-capture-*`、`.local/openviking-hooks-recall-*`。

日常只需保持本机服务运行，并在 TGame 工作区开启会话；结束会话后的长期记忆提取异步完成，不是每条消息都会立即转为长期记忆。已明确标注为临时测试的口令没有被提取为长期记忆，符合本次测试预期。

桌面端已有聊天建议重新开启会话加载插件和最新信任状态；本次端到端验收使用 Codex CLI，未把它表述为当前桌面聊天已经自动触发。插件更新或 hook 脚本变更后，可能需要再次审核信任。[Codex hooks 说明](https://learn.chatgpt.com/docs/hooks)

## 排查

- `ready=False`：先查看上述日志；若仍在初始化，等待后再次查看状态。
- 提示 1933 被占用：先确认占用进程，不结束无关进程；用本项目脚本停止其自身管理的实例后再启动。
- 模型授权失效：重新完成 Codex 正常登录后检查，不在聊天或仓库粘贴 token。
- 服务正常但记忆不自动出现：分别检查插件启用、hooks 信任和工作区采集/召回设置。

依赖使用上游提供的 CPU wheel，未启用源码构建，也未关闭 TLS 校验。[llama-cpp-python 安装说明](https://github.com/abetlen/llama-cpp-python#installation)
