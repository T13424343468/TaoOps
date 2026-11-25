# TaoOps 待办总账 v1.4（2026-11-25 汇总）

来源：老板 2026-11-25 令「全部都要 先汇总记录」。本文件为后续推进的唯一待办清单，
完成一项在台账 rectify-ledger-v320.md 记一笔并勾掉此处。

## A. Docker 仓库两件套（已拍板，最先做）

- A1 镜像加速器配置：Agent 侧管理 /etc/docker/daemon.json 的 registry-mirrors
  （改前备份、JSON 校验、改后 reload 生效），中控桥 + Docker 页可视化管理。
- A2 私有仓库登录：docker login（--password-stdin，密码仅创建时展示一次，口径同 FR-A15）、
  logout、已登录仓库列表（读 ~/.docker/config.json 脱敏展示），拉私有镜像可用。

## B. 占位页切真（10 页）

当前 main.tsx 仍挂 ModulePage 占位的路由：nodes(多机中控)、site、db、ftp、cron、env、
docker、webstats、ai、upgrade。

- B1 回归修复（优先）：docker、webstats 两页后端已落码（FR-A11/FR-A12），前端页曾在
  4c3d0da / c89b103 接真，当前路由回退为占位页——按回归恢复处理，恢复后核对功能完整。
- B2 六页切真（后端模块已有，补下发桥端点 + 真连页）：cron、site、db、ftp、env、upgrade。
- B3 两页后端也未落码：ai（AI 运维建议）、nodes（多机批量下发，跨机批量命令）。
  先补 PRD 增补设计过三路评审再编码。

## C. 体验优化三件

- C1 K8s 页接资源监控：Pod/容器级 CPU 内存用量（复用 FR-A23 采样底座）。
- C2 首页集群健康总览：K8s 集群异常状态进 Dash 一眼可见。
- C3 接入集群先试连：保存 kubeconfig 前当场验证连通性，填错立刻知道。

## D. 小尾巴两件

- D1 Docker 拉取进度结构化（层数/字节进度接口，替代原始日志输出）。
- D2 网站统计趋势图组件。

## E. 收尾账

- E1 云端实弹回归（等 Actions 额度恢复重跑 ci/cloud-linux）。
- E2 四张对账表（原型样式/界面布局/功能流程视觉/界面自动化）。
- E3 断言实效账 69 条复查。
- E4 出包日：按老板令「不急」另定。

## 执行顺序

A → B1（回归修复）→ B2 → C → D → B3 → E。
红线全绿才提交；每项完成在台账记一笔。
