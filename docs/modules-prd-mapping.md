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
| backup | FR-A9 | 网站/数据库/整站备份到本地或指定目录 | 全量+增量（size+mtime 跳过未变）；还原需 confirm=true，还原前自动回滚快照；防路径穿越 |
| docker | FR-A11 | 容器与镜像管理（list/start/stop/restart/logs/terminal/image_list/image_pull/image_remove） | docker.sock 限定 --host unix://；容器名/镜像引用/镜像源三白名单正则；逐操作审计；terminal/image_remove=Admin、启停/拉取=Operator+、读=ReadOnly |
| webstats | FR-A12 | 网站访问统计（PV/UV/状态码/流量/Top-K） | UV 用 HLL p=12（标准误差 ~1.6%）；inode+offset 断点续读轮转不重不漏；保留 180 天可配；clear/export=Admin、parse=Operator+、summary=ReadOnly |
| backup relay | FR-A13 | 节点间备份中转（源端 send → 中控 BackupRelay → 目标端装配） | 单目标并发=1、单分片在途 ≤64MB；分片+整包双 SHA-256 校验；relay_begin 磁盘预检；还原需 confirm=true+自动快照（本期仅全量）；审计进 node.cmd 哈希链；预检拒收/装配失败接告警中心 |

FR-A8（FTP，P1）仍未落码，见断点档案；FR-A9 已于 M1 联调班落码（backup 模块，8 用例全绿）。FR-A11/FR-A12/FR-A13 已落码（见整改账本第八节），FR-A14~FR-A18、FR-A20、FR-A21 按依赖排序推进中。
