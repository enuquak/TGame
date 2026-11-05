# 云端开发环境部署与维护

当前部署位于 `TGame-server:/opt/tgame`，Compose 项目名为 `tgame`。本目录保存无密钥的配置；实际版本与验收记录见 [环境配置落地记录](../docs/环境配置落地记录.md)。这是单节点开发环境。

项目统一采用 SSH 隧道供本机访问云端基础服务，不采用公网 IP 白名单直连。维护 Compose 和云安全组时，保持数据库、中间件、管理页面及知识服务的发布端口仅绑定 `127.0.0.1`，不对公网开放；云端容器之间使用内部服务地址。具体约定见 [AGENTS.md](../AGENTS.md)。

## 已有环境的日常操作

Windows 项目根目录执行：

```powershell
pwsh -NoProfile -File ./tools/scripts/powershell/Connect-Server.ps1 -RemoteCommand 'cd /opt/tgame && docker compose ps'
./tools/scripts/Start-EnvironmentTunnel.bat
./tools/scripts/Start-EnvironmentTunnel.bat -Action Status
./tools/scripts/Start-EnvironmentTunnel.bat -Action Stop
```

服务器重启后 Docker 与容器自动启动；Windows 隧道由脚本手动启动。连接已断开时先 Stop 再 Start。所有云端发布端口和本机转发端口均绑定 `127.0.0.1`。

本机浏览器打开 `http://127.0.0.1:19001`，在 Portainer 登录后选择 `TGame Docker`，即可管理这台云服务器的 Docker。转发链路为本机 19001 → 云端回环 9001 → Portainer 容器 9000；Portainer 挂载的是云服务器的 Docker socket。HTTPS 19443 仍保留，使用自签名证书。

配置改动后，先备份，再同步明确修改的文件。不要用整个目录覆盖实际 `.env`、`secrets/` 或备份，也不要使用 `docker compose down -v` 清除数据卷。同步普通配置后执行：

```bash
cd /opt/tgame
find config -type d -exec chmod 755 {} +
find config -type f -exec chmod 644 {} +
docker compose config --quiet
docker compose up -d --wait --wait-timeout 180 --pull never
```

`.env` 保持 `0600`，`secrets/` 保持 `0700`。Redis ACL 文件通过只读挂载交给容器中的 Redis 用户，因此文件为 `0444`，其父目录仍受限制。修改 `.env` 中的数据库密码不会自动更新已初始化的数据卷里的账户；密码轮换必须同步修改数据库用户和客户端配置。

## 在新服务器重建

以下是重建步骤，现有服务器已完成，不要重新初始化凭据。

1. 按 [环境配置指南](../docs/公众号/技术方案的选型/环境配置.md)安装 Docker Engine/CLI `29.9.3`、Compose `5.7.0`。本次实际使用阿里云 Docker APT 镜像，保留官方签名验证；GPG 指纹为 `9DC858229FC7DD38854AE2D88D81803C0EBFCD88`。当前 Engine、CLI、Compose 已用 `apt-mark hold` 固定，升级时需主动解除。
2. 将本目录的配置、脚本复制到 `/opt/tgame`。文件传输复用用户级 SSH 配置和 `TGame-server` 别名，不复制 SSH 私钥。
3. 在服务器执行 `python3 initialize.py`，仅首次生成随机密码与 Redis ACL；存在 `.env` 时脚本拒绝覆盖。
4. 准备 ZooNavigator 的已校验镜像，再执行 `python3 pull-images.py`。所有上游地址、架构、digest 见 `images.lock.json`。本次云端不能直接访问 Docker Hub，官方 library 镜像从 ECR 镜像源获取，其他镜像使用 DaoCloud 镜像源，均按事先从官方仓库核对的 digest 拉取。
5. 执行 `python3 prepare-zookeeper.py` 获取并校验 Apache ZooKeeper `3.8.8`。也可在 Windows 下载后经 SCP 上传到 `zookeeper/`，再执行同一脚本校验。Dockerfile 构建时再次检查固定 SHA-512，Java 17 基础镜像由 `.env` 中的 digest 固定。
6. 确认普通配置文件权限后，执行 `docker compose build zookeeper` 和 `docker compose up -d --wait --wait-timeout 180 --pull never`。
7. 执行 `python3 initialize-management.py` 初始化 Portainer 管理员和本机 Docker endpoint。脚本仅从云端回环接口操作，信任该实例的确切自签名证书，并按 Portainer 的 setup token 机制初始化。
8. 用业务账号验证 MongoDB/Redis；为 ZooKeeper 建立 `/tgame/dev` 和 `/tgame/test`。用本机 SSH 隧道验收后再交付使用。

ZooNavigator 的镜像源未收录该仓库，本次使用 `crane 0.22.1` 从官方仓库导出 `linux/amd64` 镜像，经 SSH 上传并加载。导出命令示例：

```powershell
$lock = Get-Content deploy/images.lock.json -Raw | ConvertFrom-Json
$reference = 'docker.io/elkozmon/zoonavigator@' + $lock.ZOONAVIGATOR_IMAGE.digest
./tools/crane/crane.exe pull --platform linux/amd64 $reference .local/zoonavigator.tar
Get-FileHash .local/zoonavigator.tar -Algorithm SHA256
scp -F "$env:USERPROFILE/.ssh/config" -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=15 .local/zoonavigator.tar TGame-server:/opt/tgame/
```

上传前后分别核对 SHA-256，再在云端执行 `docker load -i /opt/tgame/zoonavigator.tar`。本次归档校验值及 Docker 29 导入的 image ID 已写入 lock；`pull-images.py` 会检查该 image ID 已存在。若更换导出格式或 Docker 存储后端，不能盲目复用旧归档校验值或 image ID，必须重新核对上游 digest、归档和导入结果。镜像归档及 ZooKeeper 二进制不提交 Git。

## 备份与恢复验证

执行备份会短暂停止这 7 个服务，完成后重新启动；请安排在可中断的时间：

```powershell
pwsh -NoProfile -File ./tools/scripts/powershell/Connect-Server.ps1 -RemoteCommand 'bash /opt/tgame/backup.sh'
```

备份保存在 `/opt/tgame/backups/<北京时间时间戳>/`，包括配置、凭据与全部 7 个数据卷，附 `SHA256SUMS`。目录及归档仅管理员可读。脚本使用锁避免并发备份，停机后读取数据卷以保持文件一致性，失败时也尝试恢复服务。暂未配置定时任务或自动保留期限。

MongoDB 恢复演练使用独立临时卷和无网络临时容器，不覆盖在线数据库：

```powershell
pwsh -NoProfile -File ./tools/scripts/powershell/Connect-Server.ps1 -RemoteCommand 'bash /opt/tgame/verify-mongo-backup.sh backups/20261105-003116'
```

该脚本校验全部归档 SHA-256，再恢复 MongoDB 并检查开发、测试用户；结束时清理临时容器和卷。首次验收另加 `--expect-check-record`，确认备份中专门写入的测试记录也可读。Redis、ZooKeeper 已做服务重启持久化验证；其归档以及 Grafana/Portainer/Prometheus 归档尚未单独做完整恢复演练。

首次备份同时复制到本机 `.local/secrets/backups/20261105-003116/`。后续备份也应另存，不能只依赖服务器同盘副本。备份含密码，禁止提交仓库。
