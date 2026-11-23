# TaoOps 整改账本 · 按测试规范 v3.20（2026-11-23 凌晨整改令）

> 整改令正源：老板 2026-11-23 02:09「按照最新制度全面整改落实」。
> 本账本为 TaoOps 线整改唯一真源，随活滚动更新；收口时五张表 + 断言实效账 + 覆盖报告 SHA-256 归档。

## 一、整改基线（2026-11-23 04:30 实测）

- 全量测试：dotnet 73/73 PASS（0 FAIL），Go 16 包 ok、build/vet 干净。
- 分支覆盖账（coverlet.collector 6.0.2，cobertura 口径）：
  - **手写业务代码：行覆盖 809/1176 = 68.79%，分支覆盖 255/456 = 55.92%** —— 距 100% 有缺口，按 v3.18 第三根钉子属红灯项，补齐前不出包。
  - Grpc 生成代码：行覆盖 73/1046 = 6.98% —— **口径待老板拍板**（见下）。
- 断言实效账：全量 73 条用例逐条核对中；已修 1 条凑数断言（OneChannelFailing_DoesNotBlockTheOther 原第二条断言未重跑轮次，已补实效断言）。

## 二、待老板拍板（9.10 设计不合理即报令）

1. **Grpc 生成代码是否计入覆盖账**：protobuf 工具生成代码（AgentReflection/各消息类）非手写逻辑，无法"重构消除"。方案：覆盖账分两个口径——手写业务代码 100% 硬性达标；生成代码单列不计入总账（业界惯例）。不拍板前，按最严口径把生成代码也挂账列示。
2. **覆盖账 100% 的达标口径**：分支覆盖 55.92% → 100% 需逐模块补用例（AgentService 通道分支、Sec 检测规则、Audit 链校验分支为主力缺口）。按"存量立即补全"执行，本班起逐模块补，补齐前不出包。

## 三、五张对账表现状

| 对账表 | 现状 | 责任班 |
|---|---|---|
| 分支账 | 已出账（coverlet），手写 55.92%，缺口补全中 | 内核班（本人接手） |
| 原型样式账 | 未建——前端 17 页未产出，前端班交差后逐页对照 | 前端班交差后 |
| 界面布局账 | 未建——同上，控件树枚举后对账 | 前端班交差后 |
| 功能流程视觉账 | 未建——同上，逐屏截图落档 | 前端班交差后 |
| 界面自动化账 | 未建——同上，可达入口占比出账 | 前端班交差后 |

## 四、断言实效账

- 总账：73 条 dotnet 用例 + Go 16 包用例，逐条核对"至少一条可失败的实效断言"。
- 已核：StreamIngestorHostedServiceTests 4 条（修 1 条凑数断言）；WafManagerTests 新增 URI 绕过用例带实效断言。
- 待核：其余 69 条逐条过账，核对结果回填本节。

## 五、修红记录

1. FaultedSubscription_IsResubscribedOnNextPoll 红（期望 2 实际 1）：测试桩两路共用计数器致首订任务错配，改两路独立 TaskCompletionSource，修后绿。
2. OneChannelFailing_DoesNotBlockTheOther 凑数断言：第二条 secCalls 断言未重跑轮次，补第二轮 PollOnce 实效断言。
3. 测试产物缺失根因：一次 --no-incremental 重建被杀致 bin 清空、obj 残留 Up2Date 标记，增量构建跳过拷贝——清理 bin/obj 重建恢复。已记入构建坑清单候选。

## 六、验证回执

- 2026-11-23 04:30：dotnet 73/73 PASS；Go 16 包 ok；提交 298c5d4（A4 订阅托管服务 + A2 实弹遗留归并 + 修红）。
- 覆盖账：/tmp/cover/94cedaa5-6cf0-4399-8bda-cb0289d2afe4/coverage.cobertura.xml（归档随出包做 SHA-256）。

## 七、覆盖账终态（2026-11-23 覆盖账攻坚班）

- 全量测试：dotnet **220/220 PASS**（vstest 连跑 3 轮稳定全绿；e2e_loop 7/7、e2e_m2 9/9、Go build/vet/test 绿不变）。
- 手写业务代码口径（Grpc 生成代码单列不计入）：**行覆盖 1288/1293 = 99.61%，分支覆盖 525/544 = 96.51%**。
- 本轮补测（CoverageBackfillTests 增补 + 修路径）：账本坏行测试落盘路径修正（waf/sec/audit 子目录，此前写错路径未命中重载逻辑）、cmd 成功路径假通道回执、cmd 缺 command 400、空体/半体登录、缺口令建号、无 Bearer 前缀登出、数据目录缺省真实装配（含非 insecure 横幅）、端口 0 回退、托管服务 OCE 收尾（反射取消内部令牌命中 break）、无 logger 静默巡检、令牌 SESSION_MS 合法值、静默时段 hourOverride 缺省、AuthPolicy 零值/白名单未设、WafRuleStore FilePath + null 规则文件回退。
- **剩余缺口全部结构性不可达或纯竞态窗口，逐条挂账**：
  1. `AuthMiddleware 36B` / `AuditMiddleware 18B`：`Request.Path.Value ?? ""` —— 服务器管道中 Path.Value 恒非空，null 侧不可达。
  2. `Program 106B`：`RemoteIpAddress?.ToString() ?? ""` —— TestServer 与真实监听均恒有远端地址。
  3. `Program 122B` / `189B`：`Items["taoops.user"] ?? ""` —— 鉴权中间件对已认证请求恒写入该键。
  4. `Program 149B`（1/4）：`Enum.TryParse` 失败 → ReadOnly —— 会话角色恒为合法枚举值，失败侧管道内不可达。
  5. `SecEventStore 62B` / `WafEventStore 74B`（各 1/6）：`v.GetString() ?? ""` —— 前置 `ValueKind == String` 保证 GetString 恒非空。
  6. `StreamIngestorHostedService 14L`：静态字段初始化，类加载即命中，属统计口径；`117B`：`task.Exception?.` —— faulted 任务恒带 Exception。
  7. `NodeCommandBridge 69B`：`command ?? ""` null 侧 —— Validate 前置校验，null 命令先行抛 400，构造器不可达。
  8. `WsBridge 55/56L、109/110L`（OCE/IOException 捕获行）、`35/96B`（异步迭代器状态分支）、`117B/61B`（泵收尾 OCE 捕获）、`123B`（WS 非 Open 前置返回）：均为断连竞态窗口，确定性触发需注入桩替换真实 WS 对象，收益不抵改造风险，挂账。
- 覆盖率采集：`dotnet test --collect:"XPlat Code Coverage"`，cobertura 口径按行级合并（同文件多类条目取最大命中）。

## 八、需求变更（老板拍板，2026-11-23 晚）

- **老板拍板「全部都要，也不分期」「第三不要哈」**：第一档 9 项 + 第二档 3 项全收（镜像拉取并入 Docker 管理，共 11 个新需求点 FR-A11~FR-A21）；第三档（对象存储备份/挂载、自建邮局）明确不做。
- **流程令「先补全设计，然后按照制度推进」**：设计稿 docs/prd-v12-local-capabilities.md 已出（11 个 FR 含验收口径、协议与前端影响、依赖排序、4 个待评审问题）；三路独立评审（安全/架构/产品位，2026-11-23）均「有保留通过」，保留意见已合并修订进稿（提交 c1b6539），4 个待评审问题已裁决（中转带宽先中转+限流、SSRF 白名单 Admin 配置默认空、LE 80 端口标注+预检、统计保留 180 天可配置）；设计闸通过，按依赖排序进编码（FR-A19 告警中心先行，FR-A19 验收拆两段，推送触发随 FR-A20 补验）。
- 出包节奏：老板明确「不着急出」，11-25 出包闸顺延，新范围收口后另定出包日。
- 竞品差距清单（宝塔/1Panel 对账）已归档：第三档记入待办池永不主动推进。
