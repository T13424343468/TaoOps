# PRD v1.3 增补设计 · 功能扩展（2026-11-24 老板令「全加」）

> 令源：老板 2026-11-24 晚盘点清单「全加」+ 追问确认（一键部署口径、K8s「不分期 都要」）。
> 范围：节点离线告警、资源实时监控、应用一键部署、在线文件编辑器、定时备份策略、数据库外访管理、Docker 拉取进度结构化、统计趋势图、K8s 集成。TaoAdmin 深度集成（TaoOps 接手其部署与运维）待清单完成后另行讨论，本期仅预留模板接口。
> 硬约束不变：零服务器、零成本、无中继；平台侧不建任何云服务。
> 流程：本稿过三路独立评审后进编码。

## 〇、依赖排序（终裁）

FR-A22 节点离线告警（小活、告警体系闭环）→ FR-A25 在线文件编辑器 → FR-A26 定时备份策略 → FR-A27 数据库外访管理 → FR-A23 资源实时监控 → FR-A24 应用一键部署（最大块，模板库+升级回滚）→ FR-A28 K8s 集成（依赖 FR-A24 模板库）。小账（拉取进度、趋势图）穿插在对应模块回归时补。

## 一、需求清单（FR-A22 ~ FR-A28）

### FR-A22 节点离线告警（中控侧，P0）
- 要点：节点命令通道断连超过阈值（默认 5 分钟）产生面板告警（接 FR-A19，级别 warn），恢复后复位（边缘触发）；节点重新上线产生 info 级恢复记录。
- 验收口径：
  - 巡检复用既有节点连接状态源（NodeCommandBridge.ListNodes），不新增 Agent 通道。
  - 断连阈值 env TAOOPS_NODE_OFFLINE_THRESHOLD 可配（默认 300s）；边缘触发，恢复复位后才允许再次告警。
  - 断连告警 warn；恢复记录 info 不推送（推送仅 critical，口径同 FR-A20）。
  - 面板总览页节点列表增加连接状态标识与最近在线时间。

### FR-A23 资源实时监控（Agent 扩展 + 中控时序 + 前端图表，P1）
- 要点：CPU/内存/磁盘 IO/网络流量实时曲线（1 分钟粒度，面板展示近 1h/24h），进程 CPU/内存占用 Top10。
- 验收口径：
  - Agent 侧：sysinfo 模块扩展 op=metrics_sample（单次采样：cpu 使用率、mem total/used、disk 读写字节、net 收发字节）与 op=process_top（Top10 进程，pid/name/cpu%/mem）；采样基于 /proc，无外部依赖。
  - 中控侧：MetricsStore 内存环形缓冲（每节点 24h×60 点=1440 点/指标，超限淘汰最旧），后台服务每 60s 对在线节点采样；节点离线时段留空不补点。
  - 前端：总览节点详情出实时曲线（ECharts，近 1h/24h 切换）与进程 Top10 表。
  - RBAC：采样端点 ReadOnly 及以上。
  - 边界：历史数据不落盘（重启清零），持久化时序库记入待办池不主动推进。

### FR-A24 应用一键部署（新模块 appstore，P0）
- 要点：模板库（JSON 清单：镜像、端口、环境变量、数据卷、健康检查、依赖顺序）+ Compose 渲染 + 部署/升级/回滚/卸载生命周期管理。
- 验收口径：
  - 模板库本地内置（dataDir/appstore/templates/），首期 ≥8 个：nginx、redis、mysql、postgres、wordpress（单体+数据库）、springboot（挂 jar）、node、python。模板字段：name/version/镜像引用（多架构源）/ports/volumes/env/healthcheck/depends_on。
  - 部署：渲染 Compose 文件落盘 dataDir/appstore/instances/<name>/，经 docker.manage 既有通道执行；部署后健康检查连续 3 次通过才算成功，失败标记 failed 并告警（接 FR-A19，warn）。
  - 升级：保留上一版镜像与 Compose 文件（dataDir/appstore/instances/<name>/.previous/）；升级后健康检查不过自动回滚旧版并告警；回滚也失败则标记 broken 并 critical 告警。
  - 卸载：需 confirm=true；数据卷默认保留，勾选才删。
  - RBAC：部署/升级/卸载=Admin；查看=ReadOnly。
  - 安全：模板镜像引用走 FR-A11 镜像正则；env 中密码类字段审计脱敏；端口冲突预检（宿主机端口占用即拒绝）。
  - 微服务：多服务模板经 depends_on 排序启动，健康检查逐服务确认。
  - TaoAdmin 预留：模板机制预留「非容器原生服务」扩展位（systemd 单元模板），TaoAdmin 对接形态待其恢复后另议。

### FR-A25 在线文件编辑器（files 模块扩展，P1）
- 要点：网页编辑保存小文本文件（配置文件场景）。
- 验收口径：
  - op=file_read（≤1MB，二进制拒绝返回明确报错）与 op=file_write（≤1MB，写前自动备份同目录 <name>.bak.<ts>，保留最近 5 份）。
  - 路径校验复用 files 模块既有绝对路径/符号链接逃逸检查；RBAC：读写均 Operator 及以上（配置文件属变更动作），审计留痕。
  - 前端：文件页点开文本文件出编辑抽屉，保存前二次确认。

### FR-A26 定时备份策略（backup 模块扩展，P1）
- 要点：备份策略化——cron 表达式/间隔、保留 N 份轮转、失败告警。
- 验收口径：
  - 策略落盘 dataDir/backup/policies.json（target/type/schedule/retain）；执行经中控调度（复用 FR-A26 中控侧调度器，与 FR-A23 采样器同模式）或 Agent 侧 cron（取中控调度，统一审计与告警出口）。
  - 轮转：超出保留数的最旧备份删除，删除失败不阻塞本轮；执行失败告警（接 FR-A19，warn，边缘触发）。
  - RBAC：策略增删=Admin；手动触发=Operator+；查看=ReadOnly。

### FR-A27 数据库外访管理（db 模块扩展，P2）
- 要点：MySQL 远程访问授权/回收可视化（用户+host 粒度）。
- 验收口径：
  - op=db_grant_list/db_grant_add/db_grant_revoke（GRANT/REVOKE + FLUSH PRIVILEGES）；密码自动生成强随机仅展示一次（口径同 FR-A15）。
  - 变更前备份 my.cnf 口径不适用（纯 SQL 操作），但授权/回收逐条审计；root 及管理账号操作限 Admin。
  - 面板：授权列表 + 新增（库/账号/host/权限勾选）+ 回收（confirm）。

### FR-A28 K8s 集成（中控侧新模块 k8s，P1）
- 要点：对接用户已有集群（不装集群，对齐 1Panel 边界）；三层全做——只读接入+告警、基础操作、一键部署延伸到集群。
- 验收口径：
  - 接入：面板添加集群（kubeconfig 粘贴或上传），中控保存 dataDir/k8s/clusters/<name>.kubeconfig（600）；中控经官方 KubernetesClient 直连，Agent 不参与。
  - 安全红线：kubeconfig 只存中控、审计脱敏（不落凭据内容）、绝不下发节点 Agent；连接测试（version 探测）通过才算接入成功。
  - 只读层：节点列表/工作负载（Deployment/StatefulSet/DaemonSet）/Pod 状态/事件/Pod 日志（流式）；Pod 异常（CrashLoopBackOff/ImagePullBackOff/重启次数激增）接 FR-A19 告警（warn，边缘触发）。
  - 操作层：Deployment 扩缩容（replicas）、重启（rollout restart）、滚动更新进度（status 观察）；变更操作限 Admin，审计留痕。
  - 部署层：FR-A24 模板渲染为 K8s 清单（Deployment+Service+PVC），提交到指定 namespace；与单机模板共用模板库。
  - RBAC：集群管理（增删集群）=Admin；查看=ReadOnly；资源操作=Admin。
  - 边界：不装集群、不做多租户、不做 Helm 生态（清单直渲染），K8s 版本兼容按客户端库支持矩阵标注。

### 小账（穿插补）
- Docker 拉取进度结构化：image_pull 输出解析层数/字节进度 JSON 化（Agent 侧），前端进度条展示。
- 统计趋势图：webstats 日粒度数据前端 ECharts 出 PV/UV/流量 30 天趋势。

## 二、三路独立评审记录（2026-11-24）

- 安全位：有保留通过，4 条已并入——①FR-A25 补：二进制判定用 null-byte 探测（首 8KB 含 \0 即拒）；.bak 备份权限继承原文件（无原文件则 600）。②FR-A24 补：渲染的 Compose 文件落盘 600（env 可能含密码）。③FR-A28 补：Pod 日志查看属读操作但进审计留痕。④FR-A27 补：权限勾选粒度固定枚举（SELECT/INSERT/UPDATE/DELETE/ALL），拒绝自由拼接 SQL。
- 架构位：有保留通过，3 条已并入——①FR-A24 补：Agent 侧预检 docker compose 插件可用性，缺失给安装指引（口径同 FR-A21 certbot）。②FR-A23 补：采样失败静默跳过不告警（监控自身降级不产生告警噪音）；单节点内存占用估算 ~180KB 可接受。③FR-A26 补：中控重启后策略从落盘重载；保留份数默认 7。
- 产品位：有保留通过，2 条已并入——①FR-A25 补：超 1MB 文件面板提示走文件模块下载改。②FR-A24 补：卸载确认文案明示数据卷去留；模板详情页标注镜像架构支持（x86_64/arm64）。
- 终裁：依赖排序维持〇节；FR-A22 先行（告警闭环），FR-A24 与 FR-A28 共用模板库先行设计后编码。
