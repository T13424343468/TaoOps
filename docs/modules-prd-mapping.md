# Agent 基础层模块 ↔ PRD 对照表

| 模块 package | PRD 编号 | 需求要点 | 关键验收口径落点 |
|---|---|---|---|
| sysinfo | FR-A1 | CPU/内存/磁盘/负载/网络实时展示 | 按需采集，供面板 3 秒轮询；断连提示在传输层 |
| site | FR-A2 | 站点增删改查、域名绑定、反向代理 | 每次变更先写时间戳备份（backup 目录可查） |
| db | FR-A3 | MySQL/PostgreSQL 建删库、备份/还原 | 备份落 BackupDir 带时间戳，可还原（stdin 注入 dump） |
| files | FR-A4 | 目录树、上传下载、在线编辑、权限 | ≥1GB 大文件、4MB 分片、断点续传（manifest 记录已收分片） |
| term | FR-A5 | Web 终端会话、断线重连 | 重连令牌校验；单次 exec 无轮询，为 P99≤200ms 预留传输层预算 |
| cron | FR-A6 | Shell/备份/日志切割定时执行 | crontab 管理 + 每次执行写日志含 success/fail 状态 |
| env | FR-A7 | Nginx/Apache/MySQL/PHP/Node/Java/Docker 一键部署 | 离线包导入校验（.tar.gz）→ 解包 → install.sh，全程无外网 |
| fw | FR-A10 | iptables/firewalld 端口放行封禁 | 规则即时生效；审计留痕 + 最近一次操作可回滚 |

FR-A8（FTP，P1）与 FR-A9（备份还原）按派活单"按需"处理，本班未落码，见断点档案。
