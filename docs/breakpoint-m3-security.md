# M3 断点档案 · 中控自身安全与多用户权限审计（taoops_m3_security 班）

> 开工：2026-11-22 11:2x · 交差：12:3x（死线 15:30 前，提前约 3 小时）· 派活单正源：父代理 msg_d2b92c1ab123480c97c6e347030c7d48
> 状态：**三项交付全部完成，统一验证全绿，已提交 e8144c6**

## 入场基线复跑回执（2026-11-22 11:2x，全部真实跑过）

- `dotnet build TaoOps.slnx -c Release`：0 错 0 警（grep 计 0）
- `dotnet test`：44/44 PASS
- `go build/vet`：干净；`go test ./...`：15 包 ok
- `docs/e2e_loop.sh`：7/7 PASS；`docs/e2e_m2.sh`：9/9 PASS

## 断言清单（先于实现落盘，编码规范一.10；测试逐条对表）

- **A1 总账**：dotnet build 0 错 0 警；dotnet test 全 PASS（44 基线 + 本班新增，0 FAIL）；Go build/vet 干净、≥15 包 ok；e2e_loop 7/7、e2e_m2 9/9 回归不倒退。
- **A2 登录失败锁定（FR-C2 面板侧）**：同一用户名连续失败达阈值（默认 5 次 / 15 分钟窗）后登录被拒（locked）；白名单 IP 不触发锁定；锁定记录可查（AuthService.Lockouts + GET /api/auth/lockouts）。
- **A3 审计哈希链（FR-D4）**：AuditStore.VerifyChain 对正常账本返回 true；篡改 JSONL 账本任一历史条目后重载，VerifyChain 返回 false（篡改可检出）；审计可按用户+时间范围查询；可导出 JSON 与 CSV。
- **A4 鉴权**：未带 token 调 /api 写接口 → 401；operator 角色调管理员接口（/api/auth/users 等）→ 403；admin 持有效 token → 正常。
- **A5 密码存储**：口令以 PBKDF2-SHA256（带盐、带迭代数、版本化格式）落盘，账本文件与内存模型中无明文口令。
- **A6 SSH 防爆破（FR-C2 Agent 侧）**：解析 Failed password / Invalid user 日志行（syslog 与 RFC3339 两种时间戳格式）；按源 IP+时间窗计数；达阈值记锁定事件（TAOOPS_SEC JSONL 行，落事件日志供 LogStream 回流）；重复锁定不重复记账；白名单 IP 忽略。
- **A7 中控回流**：SecEventIngestor 解析 TAOOPS_SEC 行（跨 chunk 半行缓冲、畸形行跳过）落 SecEventStore，GET /api/sec/events 可查。

## 交付内容（对照派活单三项）

1. **中控多用户账号体系**：`server/Auth/`（UserAccount/UserStore/TokenStore/AuthService/AuthMiddleware）+ `/api/auth/*` 端点；两级角色 Admin/Operator；PBKDF2 口令哈希；token 会话（默认 8h 过期）；登录失败锁定（阈值可配、白名单 IP、锁定记录可查）；/api 写接口统一鉴权中间件（401/403）。
2. **FR-D4 权限审计**：`server/Audit/`（AuditEvent/AuditStore）+ 审计中间件（所有 /api 非读请求自动留痕：操作人/时间/IP/动作/结果）；哈希链防篡改；查询 API（用户+时间范围过滤）；导出 JSON/CSV；校验 API。
3. **FR-C2 SSH 防爆破（Agent 侧）**：`agent/internal/modules/secguard`（sec.manage：scan/lockouts），模拟日志单测覆盖；锁定事件落 TAOOPS_SEC 事件日志，中控 SecEventIngestor 经 LogStream 回流入库可查。

## 待真机验证清单

1. 真机 auth.log 实弹：/var/log/auth.log 或 /var/log/secure（TAOOPS_SSH_LOG_PATH 可配）真实 sshd 爆破样本解析与锁定。
2. TAOOPS_SEC 事件经 LogStream 回流全链路（依赖 Linux 测试目标机）。
3. HTTPS 下浏览器登录流（前端班对接后联调）。

## 已知边界（如实记录，非缺陷）

- token 会话存中控内存，中控重启后需重新登录（会话持久化列后续班评估）。
- /ws 与 gRPC 通道本班未挂鉴权（M2 通道已有 TLS+TOFU 节点侧防护；控制台 WS 鉴权随前端班补）。
- 首个管理员经 /api/auth/bootstrap 创建（仅当账号库为空时开放），避免出厂默认口令。
- 建议下版把 PRD 修订为 v1.2：中控账号体系/会话管理目前无独立 FR 条目（FR-C2 只覆盖锁定、FR-D4 覆盖审计），本班实现口径以本档案断言清单为准，待老板拍板是否补条目。

## 制度对照三行（交活单）

1. 本活上岗必读：tao-result-first（§12/§13/§16/§19）、tao-subagent-dispatch（§一3、§一13a、四·改动管理四纪律）、tao-code-standard 全文、tao-testing-standard 全文、tao-knowledge-hub（test-verify 分篇）、docs/protocol-contract.md、docs/breakpoint-m2.md、docs/breakpoint-m3-waf.md、PRD FR-C2/FR-D4——全部真读。
2. 落点：断言先行（编码规范一.10）——本档案断言清单先于实现落盘，测试逐条对表；原子写盘（三.6）——UserStore tmp+move；安全帽（十五帽.9）——口令 PBKDF2 带盐、边界校验、越权 401/403 四面过检；空吞禁令（三.2）——畸形日志行跳过但计数留痕不静默；修红不打断全测（测试规范四.5）——九处修红逐个定向回归后终版全测定版 67/67；一线一提交（改动管理四纪律）——单提交 e8144c6 成活。
3. 对照结论：无偏差。坑（dotnet run 沙箱挂起、grep -c 退出码链断）已回灌本档案修红记录；PRD 补条目建议呈老板拍板，无其他新增拍板事项。

## 验证回执（2026-11-22 12:3x，全部真实跑过；终版统一验证在修红后最终代码上完整跑）

- `dotnet build TaoOps.slnx -c Release`：**0 错 0 警**（grep ': error|: warning' 计 0；csproj TreatWarningsAsErrors=true 双保险）
- `dotnet test`：**67/67 PASS**（M3-WAF 基线 44 + 新增 AuthService 8 / AuditChain 4 / AuthMiddleware 6 / AuditMiddleware 3 / SecEventIngestor 2）
- `go build ./... && go vet ./...`：干净；`go test ./...`：**16 包 ok，0 FAIL**（新增 secguard 包 4 用例）
- `docs/e2e_loop.sh`（M1 回归）：**7/7 PASS**；`docs/e2e_m2.sh`（M2 回归）：**9/9 PASS**
- 断言 A1–A7 全绿（测试逐条对表：A2=AuthServiceTests 锁定三件套、A3=AuditChainTests 篡改检出、A4=AuthMiddlewareTests 401/403、A5=HashPassword_StoresOnlyPbkdf2、A6=secguard 四用例、A7=SecEventIngestorTests）

## 修红记录（编码期自测发现，当场修；修→定向回归→终版全测定版，未整轮重跑）

1. Program.cs 漏 `using TaoOps.Server.Sec`（CS0246）→ 补。
2. 测试工程缺 `FrameworkReference Microsoft.AspNetCore.App`（DefaultHttpContext 不可见）→ tests csproj 增 FrameworkReference。
3. 中间件测试按 3 参调 InvokeAsync（CS1501）→ 约定 `_next` 走构造器注入，改 2 参调用。
4. xUnit2013 集合断言警告（TreatWarningsAsErrors 拦下）→ 改 Assert.Single。
5. 锁定过期断言边界差 2ms（第三次失败才触发锁定，解锁点=StartMs+2+15min）→ 修测试边界并注释口径。
6. secguard 测试造数时间戳落在 Now 之后被时间窗过滤（Failures=2）→ 造数全部移到 Now 之前。
7. register_test 期望清单缺 sec.manage → 增补（模块新增的合法增量，10→11）。
8. **`dotnet run` 在沙箱静默挂起**（无输出、不监听；`dotnet build-server shutdown` 无效；直跑产物 dll 正常）→ e2e_loop/e2e_m2 起服改直跑 `bin/Release/net10.0/TaoOps.Server.dll`（M2 先例：脚本适配留痕），行为等价。
9. `grep -c` 计数 0 时退出码 1 中断 `&&` 链（统一验证脚本假失败）→ 分步重跑；经验：计数型 grep 后勿直连 &&。
