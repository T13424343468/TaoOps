# TaoOps 待办总账 v1.4（2026-11-25 汇总）

来源：老板 2026-11-25 令「全部都要 先汇总记录」。本文件为后续推进的唯一待办清单，
完成一项在台账 rectify-ledger-v320.md 记一笔并勾掉此处。

## A. Docker 仓库两件套（已拍板，最先做）✅ 2026-11-25 完成

- A1 ✅ 镜像加速器配置：Agent 侧管理 /etc/docker/daemon.json 的 registry-mirrors
  （改前备份、JSON 校验、改后需重启 Docker 生效，提供 daemon_restart），中控桥 + Docker 页可视化管理。
- A2 ✅ 私有仓库登录：docker login（--password-stdin，密码仅提交一次，不落审计）、
  logout、已登录仓库列表（读 config.json 仅回显仓库地址），拉私有镜像可用。
- 附带：Docker 页从占位切真连（容器/镜像/加速器/仓库四卡），B1 中 docker 部分一并完成。

## B. 占位页切真（10 页）

当前 main.tsx 仍挂 ModulePage 占位的路由：nodes(多机中控)、site、db、ftp、cron、env、
webstats、ai、upgrade（docker 已切真）。

- B1 更正（2026-11-25 核实）：docker、webstats 两页**并非回退丢失，而是从未接真**——
  4c3d0da / c89b103 实际只加了 mock 配置，台账原「真连页」记载不实，已更正。
  docker 页已随 A 完成切真；webstats 页仍待切真（后端已落码）。
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

## F. 面板自身补全（2026-11-25 二次盘点补充）

- F1 用户管理页：后端 /api/auth/users（列表/创建/删除）已有，前端无入口——补页面 + 导航。
- F2 修改自己密码：未见 change-password 端点，核对后补齐（含旧密码校验）。
- F3 防火墙/防篡改前端入口：fw、tamperguard Agent 模块已有，无下发桥端点、无前端页；
  先核对模块能力再决定补页面或并入安全页。
- F4 系统设置页：告警阈值等目前只能改环境变量，补可视化设置页（落盘或 env 生成）。

## 执行顺序

A → B1（回归修复）→ B2 → C → D → B3 → F → E。
红线全绿才提交；每项完成在台账记一笔。
