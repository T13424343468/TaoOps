# Web 控制台断点档案（taoops_web_r2）

> 开工：2026-11-23 04:40 · 状态：页面组①–⑦全部落码，验证全绿（回执见下）· 前班 taoops_web（23:30 派出）零产出断更，本班为 r2。

## 交付内容

工程：`src/web/`（Vite + React 19 + TypeScript + antd 6.7.1 + react-router-dom，哈希路由，中文 locale）。侧边导航四组与原型一致（中控/站点与应用/安全/运维）。

## 页面对账表（对照原型 17 页，逐页对账）

| # | 原型 section | 路由 | 数据源 | 冒烟用例 |
|---|---|---|---|---|
| 1 | 登录（非 section，登录门禁） | （门禁页） | **真连** /api/auth/login、/api/auth/bootstrap | ✅ |
| 2 | pg-dash 总览 | /dash | mock 节点/指标 | ✅ |
| 3 | pg-nodes 多机中控 | /nodes | mock（下发桥未就绪） | ✅ |
| 4 | pg-term 终端 | /term | **真连** /ws/agent/{node}/terminal | ✅ |
| 5 | pg-monitor 监控告警 | /monitor | mock | ✅ |
| 6 | pg-site 网站 | /site | mock | ✅ |
| 7 | pg-db 数据库 | /db | mock | ✅ |
| 8 | pg-files 文件 | /files | mock | ✅ |
| 9 | pg-ftp FTP | /ftp | mock | ✅ |
| 10 | pg-cron 计划任务 | /cron | mock | ✅ |
| 11 | pg-env 环境变量 | /env | mock | ✅ |
| 12 | pg-lb 负载均衡 | /lb | mock | ✅ |
| 13 | pg-ssl SSL 证书 | /ssl | mock | ✅ |
| 14 | pg-waf WAF | /waf | **真连** /api/waf/*（CRUD/启停/事件/预览/导出） | ✅ |
| 15 | pg-audit 权限审计 | /audit | **真连** /api/audit(+/verify/export)、/api/auth/users、/api/auth/lockouts、/api/sec/events | ✅ |
| 16 | pg-ai AI 运维 | /ai | mock | ✅ |
| 17 | pg-upgrade 升级 | /upgrade | mock | ✅ |
| — | pg-push 告警推送 | /push | mock | ✅ |

注：原型 17 个 section 含 push、不含备份/防火墙独立页（见设计问题 1）。冒烟测试 18 条（17 页 + 登录页签细分），全部通过。

## Mock 清单（交差必交；后端补上 HTTP 桥接后逐项替换并从两处清单同步移除）

- `GET /api/nodes`（节点注册表/心跳状态）→ `src/web/src/api/mock.ts mockNodes()`
- `POST /api/nodes/{id}/cmd`（sysinfo.overview / site.manage / db.manage / files.manage / cron.manage / env.manage / fw.manage / backup.manage / ftp.manage 下发）→ `mockDispatch()`
- `GET /api/monitor/metrics`（24h CPU/内存/网络）→ `mockMetrics()`
- AI 运维、升级与规则包、负载均衡、SSL、告警推送各页数据 → `pages/configs.ts` 对应 PAGE 配置
- 代码内同步登记：`src/web/src/api/client.ts MOCK_ENDPOINTS`

## 验证回执（真实数字）

- `npx tsc -b`：0 错（EXIT=0）
- `npm run build`（vite 生产构建）：成功，1508 modules，0 error（有 1 条 chunk>500kB 的体量提示，非编译警告，见设计问题 4）
- `npx vitest run`：**18/18 PASS**（jsdom 环境，17 页全覆盖 + 登录页签）

## 设计问题清单（按"设计不合理即报令"，带方案待拍板）

1. **备份还原（FR-A9）与防火墙/端口（FR-A10）在 17 页原型中无独立页面**。两者是 PRD P0/P1 功能。方案：原型补 pg-backup、pg-fw 两页（各一张资源表+操作按钮，复用 ModulePage 组件成本极低），或明确并入「文件」与「监控」页并在 PRD 标注归属。
2. **节点指令类页面全部依赖 mock 的根因是后端缺 HTTP→gRPC 下发桥**（中控只有 gRPC Dispatch，无 /api/nodes、/api/nodes/{id}/cmd）。方案：中控加两个薄 HTTP 端点转发 NodeCommandHub.DispatchAsync（约半班，含鉴权与审计留痕），前端即可全线切真连、mock 清单清零。
3. **ProComponents 未集成**：任务单技术栈含 ProComponents，本班用纯 antd 6 实现（antd 6 与 pro-components 兼容性未验证、包体显著增大）。方案：若拍板要用，后续把布局换 ProLayout、表格换 ProTable（预估半班），功能不受影响。
4. **单 chunk 超 500kB 提示**：antd 全量引入所致。方案：路由级 React.lazy 懒加载 + 按需优化（半班内可做），非阻塞项。

## 踩坑记录

- antd 两字中文按钮自动插空格（"备份"→"备 份"）导致文本断言失败——探针文案改四字。
- jsdom 缺 matchMedia/ResizeObserver/scrollTo——test-setup.ts 统一桩，Term 页改 scrollTop。
- npm 并行装包竞态：vitest 安装把 antd 从 package.json 剪掉（依赖竞态），重装后恢复——安装必须串行。

## 制度对照三行

1. 本活上岗必读：tao-result-first、tao-subagent-dispatch、tao-testing-standard v3.20、tao-code-standard、原型 HTML、protocol-contract.md——已真读。
2. 断言先行：18 条冒烟用例逐页对账上表；边做边提交（分组成提交）；mock 与真连边界在代码内（MOCK_ENDPOINTS）与档案双登记。
3. 对照结论：无虚报——mock 页面在页面上显式挂"数据为 mock"提示条；ProComponents 偏差如实上报（设计问题 3）。
