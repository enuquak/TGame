# YAML 配置加载与 IoC 单例 Implementation Plan

> **For agentic workers:** 使用 superpowers:executing-plans 在当前会话执行，完成后进行独立审查。用户已批准配置结构并授权实现、逐文件源码详解及推送 review-1118。

**Goal:** 按已批准的 server.yaml / ops.yaml / process.yaml 加载配置，注册进程级 IoC 单例，统一服务器、控制台和启动脚本的配置快照。

**Architecture:** Engine 提供 `YamlConfiguration`，独立解析同目录 include 文件并拒绝顶层重名，绑定类型并保存独立的合并快照。App 在日志创建后加载一次，将同一个配置对象和已校验的配置视图注入根容器；Engine 不引用 App 或业务存储类型。

**Tech Stack:** .NET 10、现有 YamlDotNet 16.3.0、Microsoft.Extensions.DependencyInjection、PowerShell 7。

**Spec:** 用户批准的 `code/server/config/server.yaml` 注释及本会话约定。

## Global Constraints

- 复用 `review-1118`，保留用户对 Program.cs 日志初始化顺序的调整。
- include 使用无扩展名的同目录名称；各文件单独解析，锚点不跨文件；顶层重名拒绝覆盖。
- 保留启动数量、首次连接门槛、拓扑无环、ZooKeeper/ID 生命周期及基础框架启动范围。
- 只运行必要的小范围检查，不运行真实外部服务或完整长测试。
- 文档覆盖全项目手写源码，排除工具脚本、生成代码和构建产物。
- 完成实现、文档、检查和提交后，推送 origin/review-1118，不合并 main。

## Review Focus

- include 缺失、非法名称、重复字段及跨文档锚点必须明确失败。
- 本轮快照不得依赖后续源文件变更，启动数量与运行时门槛必须一致。
- 同一容器内配置服务唯一，不同容器和不同配置文件互相独立。
- 脚本的首次启动、补起、停止及控制台会话恢复使用相同配置来源。
- 基础框架不因可选 MongoDB/Redis 缺少凭据而连接或初始化业务存储。

## Task 1: YAML 解析与配置绑定

**Files:** Engine/configuration、App/ServerSettings.cs、各 Options、EngineTests 配置检查。

**Interfaces:** `YamlConfiguration(string path)` 加载；`Read<T>(string? section = null)` 绑定；`SaveSnapshot(string path, string? runDirectory = null)` 保存合并快照；`ResolveRunDirectory(string workingDirectory)` 解析运行目录。现有拓扑和数量类型从同一配置读取，继续保留独立验证。

- [x] 添加 include、锚点、重名/缺失输入、独立快照的失败检查。
- [x] 实现解析服务、类型绑定及配置模型，保持原配置值。
- [x] 运行针对性的 YAML 配置检查。

## Task 2: IoC、宿主与启动工具

**Files:** Program.cs、ServerServices.cs、项目输出配置、ConsoleSession.cs、PowerShell 启动辅助及对应短测试。

- [x] 将统一配置实例注册到 IoC，验证相同容器共享实例、不同容器相互独立。
- [x] 适配控制台和脚本的加载、快照与恢复，移除旧 JSON 配置读取路径。
- [x] 运行配置/IoC/日志检查、控制台 state-only 检查和受影响脚本的短检查。
- [x] 同步协作规范、入口和使用文档中的旧文件名与加载约定。

## Task 3: 源码详解、审查与推送

- [x] 冻结最终手写源码清单，逐文件生成对应详解及总索引。
- [x] 核对文档覆盖率、链接和实现一致性。
- [x] 独立审查本次改造并修正实际问题，只追加必要验证。
- 最后交付步骤：核对并提交本次文件，推送 origin/review-1118，确认远端提交一致；提交与推送结果以 Git 记录及最终回复为准。

## Execution Ledger

- 2026-11-19：已确认当前仅 Program.cs 存在用户未提交修改；配置草案已提交为 8d404a5。保留用户改动并在该分支继续。
- Ruling: 使用当前事项分支直接执行，用户已明确授权实现和推送，不再次要求方案确认。

- 2026-11-19：完成统一 YAML 加载、宿主绑定与 IoC 单例，启动器保存完整独立快照；保留既有进程连接和就绪语义。
- 验证：通过 `--yaml-configuration`（含 SettingsChecks）、`--configuration`、`--ioc`、`--logging`、`--mongo-storage`；控制台 `--state-only` 通过；PowerShell 类型加载及脚本语法通过；`Test-ServerScripts.ps1` 的隔离假进程检查通过。未运行完整测试或真实 ZooKeeper/MongoDB/Redis 联调。
- 审查：独立审查发现源 include 损坏会阻断脚本管理旧组，已改用固定 `logs/tools/server-scripts/current` 和锁；新增删除 ops.yaml 后补起与停止检查通过，复核无剩余重要问题。
- 文档：78 个手写 C#/XAML/proto 文件对应 78 篇详解，总索引为 `docs/代码详解/README.md`；覆盖率及全部相对链接检查通过，工具脚本/生成代码/构建产物排除。
- 收尾规则：沿用用户明确授权直接推送 review-1118；不追加完整测试、不合并 main、不新建 PR。
