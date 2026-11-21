# M3 断点档案 · WAF 管理面与 OpenResty 配置生成（taoops_m3_waf 班）

> 开工：2026-11-22 07:2x · 交差：09:0x（死线 11:15 前）· 派活单正源：父代理 msg_4c7b97dec9934453a60981b10550d262

## 状态：五项验收全部达成，统一验证全绿，已提交

## 交付内容（对照验收标准）

1. **WAF 规则模型（FR-B1 口径）** ✅ `server/Waf/WafRule.cs`：类目 Sqli/Xss/Scanner/CcRate/IpBlacklist/IpWhitelist/Custom（OWASP Top 10 由类目+Custom 正则承载）；规则可启停（Enabled）、可排序（Priority 降序进产物）、可全局开关（WafSettings.Enabled）。
2. **中控 WAF 管理 API** ✅ `Program.cs` 挂 `/api/waf/rules` CRUD（POST/PUT/DELETE/PATCH enabled）、`/api/waf/settings`、`/api/waf/rules/export|import`、`/api/waf/events`、`/api/waf/config/preview`；存储 `WafRuleStore`（JSON 真源，tmp+move 原子写盘，重复 Id 拒绝，坏导入抛错）。
3. **OpenResty 配置生成器** ✅ `server/Waf/OpenRestyConfigGenerator.cs`：输入规则集，确定性输出四件产物——http 级 `waf.conf`（lua_package_path+init_by_lua 全局开关）、server 级站点片段（access_by_lua_file）、`lua/access.lua`（白名单短路→黑名单/正则/CC 拦截→记录，rules.json mtime 变化 5 秒内热重载，换规则不动代码）、`lua/rules.json`（camelCase 数据文件）。产物快照断言锁定格式（A1–A5）。
4. **拦截日志回流** ✅ `server/Waf/WafEventIngestor.cs` + `WafEventStore.cs`：复用 M2 LogStream 通道订阅节点 WAF 日志（默认 `/var/log/taoops-waf/events.log`，可 TAOOPS_WAF_LOG_PATH 覆盖），按行解析 TAOOPS_WAF JSON 事件（跨 chunk 半行缓冲）落 JSONL 账本；查询按节点+时间范围过滤；畸形行跳过不崩。Agent 侧零改动（logs 模块已支持任意文件 tail）。
5. **统一验证全绿** ✅ 见下方回执。

## 验证回执（数字，全部真实跑过）

- `dotnet build TaoOps.slnx -c Release`：**0 错 0 警**（grep ': error|: warning' 计 0）
- `dotnet test`：**44/44 PASS**（M2 基线 24 + 新增 WafManagerTests 12 + WafEventIngestorTests 8）
- `go build ./... && go vet ./...`：干净；`go test ./...`：**15 包 ok，0 FAIL**（本班 Go 侧零改动，回归确认不倒退）
- `docs/e2e_loop.sh`（M1 回归）：**7/7 PASS**
- `docs/e2e_m2.sh`（M2 回归）：**9/9 PASS**
- 断言清单 A1–A9 全绿（正源：本档案断言清单节；测试 `WafManagerTests`/`WafEventIngestorTests` 逐条对表）

## 修红记录（编码期自测发现，当场修）

1. 产物序列化口径不统一（PascalCase vs Lua 侧 camelCase）→ rules.json 统一 camelCase，Lua 模板字段对齐（windowSeconds/maxRequests）。
2. 初版缺 server 级 conf 片段（A1 断言拦下）→ 生成器补 SiteConf 产物。
3. 事件账本重载解析与落盘口径不对称（TryParseEvent 读 snake_case 日志行，落盘是 WafEvent JSON）→ 重载改 Deserialize 对称口径。
4. match_regex 里 subject 重复拼接 http_user_agent（笔误）→ 修正。
5. 断言自身缺陷 1 处（Query 过滤断言漏算无节点过滤的第三条）→ 修正断言。

## 待真机验证清单（OpenResty 实弹，沙箱无 OpenResty——apt 源无包、外网源不可达，已实测留痕）

1. 真机装 OpenResty 后：`opm get` 或源码装 lfs（access.lua 的 mtime 检查依赖 luafilesystem；无 lfs 时热重载退化为启动加载，功能不断）。
2. 装配步骤：`waf.conf` 放 http 块 include；站点片段放 server 块；`lua/` 两文件落 `/etc/openresty/taoops-waf/lua/`；`lua_shared_dict taoops_waf_cc 10m;` 需在 http 块补一行（CC 计数用，产物注释已留 TODO 说明）。
3. 实弹断言：SQLi 样本（`?id=1 union select`）被 403、白名单 IP 放行、CC 超限拦截、`/var/log/taoops-waf/events.log` 出现 TAOOPS_WAF 行、中控 `/api/waf/events` 经 Agent 日志回流查到事件。
4. 热更新：改 rules.json 后 ≤5 秒生效，无需 reload nginx。

## 还差什么 / 下一步（M3 后续班）

- WAF 管理界面（Web 控制台页）未做——API 已就绪，前端班直接对接。
- eBPF 防篡改、多用户权限、审计（M3 后续范围）未动。
- 入场基线（下一班）：dotnet 44/44、Go 15 包、e2e 7/7+9/9 全绿为本班交付后基线。

## 制度对照三行（交活单）

1. 本活上岗必读：tao-result-first（§12/§13/§16/§19）、tao-subagent-dispatch（§一3、§一13a、四·改动管理四纪律）、tao-code-standard 全文、tao-testing-standard 全文、docs/protocol-contract.md、docs/breakpoint-m2.md、PRD FR-B1——全部真读。
2. 落点：断言先行（编码规范一.10）——断言清单先于实现落盘（本档案），测试逐条对表；原子写盘（三.6）——Store tmp+move；写活纪律（一.9）——规则是数据（rules.json）不是代码；修红不打断全测（测试规范四.5）——五处修红逐个定向回归后全测定版 44/44；失败即取证（四.7）——每处失败当场定位根因再修，未重盘。
3. 对照结论：无偏差。坑（JSON 转义断言口径、序列化命名策略不对称）已回灌本档案修红记录；无新增需老板拍板事项。
