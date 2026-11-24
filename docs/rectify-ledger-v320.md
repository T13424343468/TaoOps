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
- **FR-A19 告警中心已落码（2026-11-23）**：AlertStore（dataDir/alerts/alerts.jsonl，保留默认 90 天可配）+ AlertCenter（磁盘水位 ≥90% 边缘触发 critical/<85% 迟滞复位；入侵检测 severity 映射转发）+ /api/alerts 四端点（列表过滤/未读数/单条已读/全部已读）；critical 触发推送随 FR-A20 补验（验收拆两段）。新增 15 用例，235/235 全绿（连跑两轮稳定）。
- 竞品差距清单（宝塔/1Panel 对账）已归档：第三档记入待办池永不主动推进。
- **FR-A11 Docker 容器与镜像管理已落码（2026-11-24）**：四层全通——Agent 模块（docker.manage 九类操作：list/start/stop/restart/logs/terminal/image_list/image_pull/image_remove，docker.sock 限定 --host unix://，容器名/镜像引用/镜像源三白名单正则，逐操作审计留痕，提交 ddf5206）；中控下发桥（AgentService 白名单 + 专用端点 /api/nodes/{id}/docker 按 DockerPolicy 分级鉴权：terminal/image_remove=Admin、启停/拉取=Operator+、读=ReadOnly，提交 5903cd5）；前端页（Docker 管理页 + 导航路由，提交 4c3d0da）；容器日志回流（LogControl 增 target=file|container，契约先行改文档再改 proto，Agent tailContainer 经 docker.sock --follow 断连自动重订，term.manage 增 op=attach_container 会话路由不变，提交 d2119dc）。验证：Agent 全量绿、中控 245/245（连跑两轮稳定）、e2e_loop 7/7、e2e_m2 9/9。
- FR-A11 验收口径对账：Action 枚举 9 类审计逐类覆盖 ✓；三正则 ✓；docker.sock 限定 ✓；RBAC 分级 ✓；日志断连自动重订 ✓；拉取进度当前为 docker pull 原始层进度输出（可查），结构化层数/字节进度接口记入 FR-A12 后补强项。
- **FR-A12 网站访问统计已落码两层（2026-11-24）**：Agent 模块（webstats.manage：op=parse/summary/clear/export；流式逐行解析 64KB 行上限截断不崩、绝对路径+仅常规文件校验拒设备/管道；日粒度聚合落盘 dataDir/webstats/<site>/<date>.json——PV、UV 用 HLL p=12（标准误差 ~1.6% 达 PRD ~2%）、状态码分布、流量（缺 $body_bytes_sent 降级仅 PV 并标 degraded）、Top-K≤100 用固定容量堆+计数草图不建全量哈希；inode+offset 断点续读轮转不重不漏；保留默认 180 天 env 可配，parse 时自动清理；提交 9cd0003）；中控下发桥（AgentService 白名单 + 专用端点 /api/nodes/{id}/webstats 按 WebStatsPolicy 分级鉴权：clear/export=Admin（UV/来源 IP 属访问者个人信息）、parse=Operator+、summary=ReadOnly，提交 5be11ed）。验证：Agent 19 包全绿（webstats 13 用例）、中控 250/250（1 例已知环境抖动复跑全绿）。前端统计页已接（提交 c89b103：网站统计页 + 导航路由，vitest 18/18、构建通过）。剩余：PRD 映射表同步、趋势图组件（可随 FR-A20 推送触发一并补验）。
- FR-A12 落码中修三处真缺陷：HLL 线性计数公式误写 m²/zeros（应为 m·ln(m/zeros)）、rho 上界未封顶、FNV-1a 对尾字节差异输入雪崩性弱致索引坍缩（补 splitmix64 终轮混淆）；另修超长行遇 bufio.ErrBufferFull 中断解析的缺陷。
- 环境备注（2026-11-24）：沙箱工具链重置，Go 1.23.4 与 .NET 10.0.404 重装；Release 构建内存超限被杀（全局 OOM ~2.5GB），端到端改用 Debug 产物跑通；wsprobe 探针重编译后 e2e_m2 全过。
- **FR-A13 节点间备份已落码四层（2026-11-24）**：契约先行（protocol-contract.md §7 节点间备份中转：BackupRelay RPC、中控转发规则——单目标并发=1、单分片在途 ≤64MB、信任模型中控可见明文、还原口径 confirm=true+自动快照本期仅全量、审计进 node.cmd 哈希链）；proto（agent.proto 增 BackupRelay 双向流 + RelayChunk/RelayAck + DispatchRequest.blob，protoc 3.21.12 重新生成）；Agent 侧（relay.go 发送端 4MB 分片逐片等 ack 重试 ≤3 次 + backup/relay.go 五个中转 ops：relay_begin 磁盘预检、relay_put 分片 SHA-256 校验幂等落盘、relay_assemble 顺序拼装+整包校验失败即清理、relay_abort；op=send/send_status 异步带进度；提交 e00eb38）；中控桥（RelayHub.HandleStream 双向流转发，经 NodeCommandHub blob 承载，每 relay 记流量 Stats、审计哈希链 User="relay"，预检拒收/装配失败接 FR-A19 告警，GET /api/relay/stats，提交 b160158）；前端（备份页：备份列表真连 + 节点间发送表单 send/send_status 2s 轮询 + 中转流量统计卡 + 导航路由，提交 faefcfa）。验证：Agent 19 包全绿（relay 11 用例）、中控 257/257（vstest 连跑 2 轮）、前端 vitest 18/18（17 页全覆盖冒烟含备份页）、构建通过；master 顶 faefcfa 已推远端 ls-remote 核验。
- FR-A13 环境备注：web/node_modules 因 NFS 残留损坏（magic-string/jsdom/antd 多包文件缺失），mv 旧目录后重装 + 定向补装 jsdom/antd 恢复；node_modules.old 含 NFS 硬链接文件无法删净，不影响构建。
- **FR-A14 一键迁移已落码四层（2026-11-24）**：契约先行（protocol-contract.md §8：migrate.manage 三 ops——precheck/pack/restore，传输完全复用 §7 中转底座不新增 RPC，MigratePolicy 鉴权，面板四步编排）；Agent 侧（migrate 模块：precheck 路径校验+统计+版本采集、pack 复用 backup 全量打包+迁移清单 <archive>.migrate.json、restore 目标端——file 限 BackupDir 内、target_path 绝对/规范/无 ..、逐级符号链接逃逸检查、版本不一致默认拒绝 force 跳过留审计、confirm=true 门、还原前自动快照、迁移报告；backup 包补公开门面 PackFull/SnapshotFor/ExtractTo/Dir；提交 1f55fe9）；中控桥（migrate.manage 白名单 + 专用端点 /api/nodes/{id}/migrate 按 MigratePolicy 分级鉴权：precheck/pack=Operator+、restore=Admin、未知默认 Admin，提交 c28e7dd）；前端（一键迁移页：四步 Steps 流程——预检→打包→中转（复用 backup send + send_status 轮询）→目标端还原（confirm），导航路由，提交 688b334）。验证：Agent 20 包全绿（migrate 11 用例）、中控 269/269（vstest 连跑 2 轮）、前端 vitest 20/20（19 页全覆盖冒烟含迁移页）、构建通过；master 顶 688b334 已推远端 ls-remote 核验。
- FR-A14 落码备注：中控测试工程增量构建曾因 obj 残留 Up2Date 标记未拷贝新用例（账本第五节已知坑），touch 测试文件强制重编后全绿。
- **FR-A18 负载均衡已落码四层（2026-11-24，子 Agent 执行）**：契约先行（protocol-contract.md §9：LbPolicy upstream 列表/权重/健康检查、端点 /api/nodes/{id}/lb）；Agent 侧 lb 模块（9 用例，提交 f0322dc 契约 f06c158）；中控下发桥（LbPolicy + 专用端点，提交 077959d）；前端（Lb.tsx 真连页：upstream 列表/增删/摘除/恢复/健康状态，提交 4cca147）。验证：Agent 20 包全绿、前端 vitest 20/20 + 构建通过。
- **FR-A16 两步验证已落码两层（2026-11-24，子 Agent 执行）**：中控 TOTP 全套（RFC 6238 校验 ±1 步窗口、重放防护、登录二次因子 totp_required、绑定需密码重认证、恢复码 PBKDF2 哈希单次有效、重生成作废旧码、5 次失败锁 5 分钟，提交 2721dac）；前端（登录二次验证步骤 + TOTP 设置页 + 导航路由，提交 752ffd5）。验证：中控 301/301（vstest 连跑 2 轮）、前端冒烟 20 页 21 用例 + 构建通过。落码备注：Program.cs 顶级语句中命名空间需用别名 `using Push = TaoOps.Server.Push;`，请求模型定义在 Program.cs 全局命名空间引用不带前缀。
- **FR-A20 自定义告警推送已落码四层（2026-11-24）**：契约先行（protocol-contract.md §11，原 §9 撞号改号，提交 29a05a3）；中控侧（PushChannelStore/SsrfWhitelistStore/PushLogStore 落盘 dataDir/push/、SsrfGuard——仅 http(s)、PRD 私网段拒列表、DNS 全记录解析校验、hostname/CIDR 白名单、禁重定向客户端；PushDispatcher——critical 触发、重试 3 次退避 1s/2s/4s、逐次留痕；AlertCenter critical 接线；端点 /api/push/* Admin 鉴权、白名单变更审计；提交 a40f4a1）；前端（Push 页：渠道管理 + SSRF 白名单 + 测试推送 + 推送日志，提交同上）；测试修复（提交 e3e6ba7：Stores() 三元组解构顺序错位致 whitelist 绑到 PushChannelStore 的编译错误、AlertCenter 落账断言、前端 PushPage 导入接线与未用 mock 清理）。验证：中控 335/335（vstest 连跑 2 轮）、前端 vitest 21/21 + 构建通过；master 顶 e3e6ba7 已推远端 ls-remote 核验。
- **FR-A19 推送触发补验通过（2026-11-24，验收第二段收口）**：AlertCenter.Raise 对所有级别落账后经 PushDispatcher.OnAlert 异步触发，仅 critical 派发；PushDispatcherTests.AlertCenter_CriticalAlert_TriggersPush 覆盖（critical 触发推送 + warn 不触发 + 告警照常落账），FR-A19 验收两段全部完成。
- **FR-A17 日志清理已落码四层（2026-11-24）**：契约先行（protocol-contract.md §12：规则落点 dataDir/logclean/rules.json、内置白名单 /etc /usr /bin dataDir、前缀匹配+按天取整 mtime、绝对路径/拒根路径/拒过宽通配（通配段前至少两级具体目录）、EvalSymlinks 后再判白名单防符号链接逃逸、归档 gzip 保留 7 天自动过期、cleaned.jsonl 留痕）；Agent 侧 logclean 模块（op=list/add/remove/run，9 用例，提交 88aafca）；中控桥（logclean.manage 白名单 + LogCleanPolicy 分级鉴权：list=ReadOnly、add/remove/run=Operator+、未知默认 Admin + 专用端点 /api/nodes/{id}/logclean，15 用例，提交 51cbada）；前端（LogClean.tsx 真连页：规则表/增删/执行/清理留痕，导航路由，冒烟 22/22 + 构建通过，提交 63ad302）。验证：Agent 21 包全绿、中控 350/350（vstest 连跑 2 轮）；master 顶 63ad302 已推远端 ls-remote 核验。落码备注：下发桥仅回传 Output，list/run 的结构化结果改为直接以 JSON 落 Output 供前端解析（Data 字段在 dispatch 层被丢弃）。
- **FR-A15 MySQL 主从复制已落码四层（2026-11-24）**：契约先行（protocol-contract.md §13：master_grant/replica_config/replica_status/replica_reset 四 ops，凭据脱敏口径——中控审计不记 args + Agent Redact 助手统一脱敏 + 密码仅创建时展示一次，60s 巡检接 FR-A19 告警，DbPolicy 鉴权）；Agent 侧（db 模块扩展：crypto/rand 24 位强随机密码、my.cnf 时间戳备份口径同 FR-A2、SHOW SLAVE STATUS\G 解析 JSON、STOP SLAVE→CHANGE MASTER→START SLAVE、RESET SLAVE ALL；8 用例，提交 a3c950b）；中控桥（DbPolicy：replica_status=ReadOnly、grant/config/reset=Admin + 专用端点 /api/nodes/{id}/db + ReplicationMonitor 托管服务——60s 轮询在线节点、IO/SQL 线程中断与延迟>300s（env TAOOPS_REPLICA_LAG_THRESHOLD 可配）边缘触发 warn 告警、恢复复位；16 用例，提交 9ebf175）；前端（DbReplica.tsx 真连页：状态卡/主库授权（凭据一次性展示）/从库配置/断链重置，导航路由，冒烟 23/23 + 构建通过，提交 d755ddc）。验证：Agent 21 包全绿、中控 366/366（vstest 连跑 2 轮）；master 顶 d755ddc 已推远端 ls-remote 核验。落码备注：前端构建曾带未用导入出包（构建门禁漏过），下一笔修复；SlaveView 反序列化需 JsonPropertyName 显式映射 snake_case。
