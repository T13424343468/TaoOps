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
- ~~B3 两页后端也未落码：ai（AI 运维建议）、nodes（多机批量下发，跨机批量命令）~~——已收口（2026-11-26，PRD v1.4 过三路评审，提交 fd080ab/b8dd494/58c7be3，台账在案）。
  先补 PRD 增补设计过三路评审再编码。

## C. 体验优化三件

- C1 ✅ K8s 页接资源监控：Pod/容器级 CPU 内存用量（metrics.k8s.io CustomObjects，未装 metrics-server 降级 Available=false+note；提交 8795e60）。
- C2 ✅ 首页集群健康总览：GET /api/k8s/health + Dash「K8s 集群健康」卡片（单集群失败降级 Error；提交 8795e60）。
- C3 ✅ 接入集群先试连：核实已有——TestVersion 先测后存 + connect_failed 审计测试在账（K8sTests.cs:340；提交 8795e60 注明）。

## D. 小尾巴两件

- D1 ✅ Docker 拉取进度结构化：ParsePullProgress 按层去重保留最终状态，PullProgressJSON 落 Output，Docker.tsx 进度表（兼容旧版降级；提交 4d2c7bb）。
- D2 ✅ 网站统计趋势图组件：TrendChart.tsx（echarts SVG 折线 PV/UV）+ Webstats.tsx 接入（提交 100f07d）。

## E. 收尾账

- E1 ✅ 云端实弹回归：run 45590430282 全绿 18 步（node_id 错配修复后，关5/关6/关7 实弹证据在案；提交 3a2af5f）。
- E2 ✅ 四张对账表：docs/e2-reconciliation-v1.md（29 页实拍、原型 17/17 一致、21 流程通过、vitest 33/33 + 导航可达 100%；实拍抓出并修复 WAF 页崩溃，提交 3ac8f4a）。
- E3 ✅ 断言实效账 69 条复查：全部有实效断言，0 凑数、0 修红（台账在案）。
- E4 出包日：按老板令「不急」另定。

## F. 面板自身补全（2026-11-25 二次盘点补充）

- F1 ✅ 用户管理页：Users.tsx 真连 /api/auth/users + 路由/导航接线（提交 ddcf791）。
- F2 ✅ 修改自己密码：POST /api/auth/change-password（旧密码校验+新密码≥8 位+审计）+ UpdatePasswordHash + 顶栏改密码弹窗，端点集成测试 4 例（提交 5959c5b/ddcf791）。
- F3 ✅ 防火墙/防篡改前端入口：/api/nodes/{id}/fw 与 /tamperguard 端点（FwPolicy/TamperGuardPolicy 分级鉴权）+ Fw.tsx 页（audit/allow/deny/remove/rollback+status，Output JSON），矩阵测试+Go Output 断言（提交 45d68ca/47af528）。
- F4 ✅ 系统设置页：SettingsStore（settings.json 原子写盘，损坏回退默认）+ AlertCenter 实例阈值热重载 + GET/PUT /api/settings/alerts（PUT Admin only）+ Settings.tsx 表单页（提交 5959c5b/9345194）。

## 执行顺序

A → B1（回归修复）→ B2 → C → D → B3 → F → E。
红线全绿才提交；每项完成在台账记一笔。

## G. Windows 版中控（2026-11-27 老板令「win也要，就这个版」）

- G1 ✅ Windows 原型定版：安装向导 6 步（默认盘符 D、目录可改）+ 服务自启页 + 升级回滚页 + 完全卸载页，视觉评分 16 屏满分（草案口径），三视角评审有条件通过，老板 2026-11-27 拍板定版。
- G2 ✅ 中控 Windows 支持（FR-A31，提交 43f91ff）：AddWindowsService 服务壳（服务名 TaoOps，Linux 不受影响）；数据目录平台化（Windows %ProgramData%\TaoOps\data）；界面自带托管（web/dist 随中控分发，静态+SPA 回退）。测试 770/770 全绿（新增 9 条平台测试）。
- G3 ✅ 安装器与卸载脚本：installer/windows/taops.iss（默认 D:\Program Files\TaoOps 可改；装服务自启+防火墙放行；卸载六项全清：服务/自启/注册表/防火墙/文件/数据目录可选保留）+ scripts/publish-windows.sh 发布脚本。
- G4 待办：Windows 真机验证（服务注册/自启/卸载残留核查）——需 Windows 环境，云测或老板提供机器后执行。
