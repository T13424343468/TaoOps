# M3 断点档案 · 企业层班（taoops_m3_enterprise_r3）

> 开工：2026-11-22 晚 · 死线：2026-11-23 08:00 · 派活单正源：父代理 msg_fe4bfe603e624a018f93757738ed2cc4（第 3 次重开）
> 状态：进行中（本档案随活滚动更新，收口时定稿）

## 入场基线（2026-11-22，接手时复跑）

- dotnet build/test：67/67 PASS，0 错 0 警（前班回执，接手复跑见验证回执节）
- Go build/vet 干净，16 包 ok；e2e_loop 7/7、e2e_m2 9/9 PASS

## 断言清单（先于实现落盘，编码规范一.10；测试逐条对表）

- **A1 总账**：dotnet build 0 错 0 警；dotnet test 全 PASS（67 基线 + 本班新增，0 FAIL）；Go build/vet 干净、≥16 包 ok；e2e_loop 7/7、e2e_m2 9/9 回归不倒退。
- **A2 实弹遗留归并**：master 的 ModuleWhitelist 含 sec.manage（实弹班 119ab1a 同款修复）；access.lua 产物为 do-block 内联执行体（非 return 闭包，ed407e3 同款）；ngx.re 走全局（974cc3a 同款）；快照单测同步更新。
- **A3 URI 编码绕过修复**：match_regex 对 request_uri 先 ngx.unescape_uri 再匹配——`?id=1%20union%20select` 不再绕过 SQLi 规则；日志仍记原始 URI（取证口径）。
- **A4 Ingestor 自动订阅**：WafEventIngestor 与 SecEventIngestor 由后台托管服务在新节点上线后自动订阅，无需手工触发；节点流未就绪时优雅重试不崩；单测覆盖订阅去重与失败重试。
- **A5 eBPF 防篡改接口抽象**：tamperguard 包定义 Probe 接口（Start/Events/Stop）与 BpfLoader 抽象；BpfProbe 加载失败返回错误而非 panic；Guard 捕获后降级为轮询看护并产出 tamper.degraded 事件（TAOOPS_SEC 通道回流中控）。
- **A6 防篡改事件**：看护路径（TAOOPS_TAMPER_PATHS，逗号分隔，文件或目录）内容变更产出 tamper.change 事件（路径/操作/来源/时间戳），落 TAOOPS_SEC 事件日志；表驱动测试覆盖配置解析、变更检出、降级路径、事件行格式。
- **A7 tamper.guard 模块命令**：op=status 返回当前探针模式（ebpf/poll/degraded）、看护路径与事件计数；注册进 registry（register_test 11→12）；中控白名单放行。
- **A8 可疑行为检测——异常登录时段**：成功登录发生在静默时段（TAOOPS_SEC_QUIET_HOURS，默认 23-06，跨零点窗口）外 → 产出 sec.alert（type=login_hour）进 FR-D4 审计链。
- **A9 可疑行为检测——非常规命令**：用户首次执行某类写操作（该用户历史未见过的 action）→ 产出 sec.alert（type=unusual_action，severity=low）；同用户重复执行同 action 不重复告警。
- **A10 暴力尝试聚合告警**：同源 IP 的 SEC 锁定/失败事件在窗口（TAOOPS_SEC_AGG_WINDOW_MS，默认 15 分钟）内达阈值（TAOOPS_SEC_AGG_THRESHOLD，默认 3）→ 聚合为一条 sec.alert（type=bruteforce_agg，severity=high），窗口内不重复；面板侧登录锁定同样计入。
- **A11 告警可查可溯**：GET /api/sec/alerts 按 type/severity/时间过滤查询；每条告警在审计哈希链中（篡改可检出，复用 VerifyChain）。
- **A12 只读角色**：UserRole 增 ReadOnly；只读角色对一切写方法（POST/PUT/DELETE/PATCH）403、读方法 200；创建用户接口接受 "ReadOnly"。
- **A13 权限点与资源可见性**：权限点常量集中定义（Permissions.cs），角色→权限矩阵静态可查；GET /api/auth/permissions 返回矩阵（登录后可查）；/api/auth/me 附带权限清单；越权路径全测（admin/operator/readonly × 代表端点矩阵）。
- **A14 界面覆盖自查**：对照 PRD v1 与全套原型列界面/功能覆盖情况表；设计不合理处带方案写入"设计优化建议"节上报，不自行改原型。

## 交付内容（对照派活单六项）

1. **eBPF 内核级防篡改**（A5-A7）：agent/internal/tamperguard 包——Probe 接口 + BpfLoader 抽象 + BpfProbe（真机加载路径见下）+ PollWatcher 降级看护 + Guard 管理器；事件经 TAOOPS_SEC 通道回流；tamper.guard 模块命令。
2. **防入侵基础**（A8-A11）：server/Sec/IntrusionDetector——异常时段登录、非常规命令、暴力尝试聚合三类规则；告警进审计哈希链；/api/sec/alerts 查询。
3. **多用户权限完善**（A12-A13）：ReadOnly 角色、权限点矩阵、资源可见性、越权全测。
4. **界面覆盖自查**（A14）：覆盖对照表 + 设计优化建议（本档案）。
5. **实弹遗留归并**（A2-A4）：sec.manage 白名单、access.lua 闭包修复、ngx.re 全局、URI 解码绕过修复、Ingestor 自动订阅接线。
6. **统一验证与收口**：见验证回执节。

## eBPF 真机/云端加载路径（沙箱无内核加载条件，落码口径）

- 沙箱约束：无 CAP_BPF/CAP_SYS_ADMIN、/sys/fs/bpf 不可写、无内核头——BpfProbe 加载必然失败，属预期降级路径（派活单明确"优雅降级不算红"）。
- 实弹环境（cloudtest 档案口径）：kernel 7.0.0-azure、bpftool v7.7.0、runner 有 sudo、/sys/fs/bpf 空、securityfs 未暴露。
- 真机加载步骤（部署文档口径，随本档案交付）：
  1. `clang -O2 -target bpf -c tamper_probe.c -o tamper_probe.o`（内核头需在目标机；tracepoint/kprobe 挂 openat/unlink/rename）。
  2. `sudo bpftool prog load tamper_probe.o /sys/fs/bpf/taoops_tamper`（bpftool v7.7.0 支持；securityfs 未暴露不影响 bpftool 走 bpf syscall）。
  3. Agent 配置 `TAOOPS_TAMPER_BPF_PIN=/sys/fs/bpf/taoops_tamper`，BpfProbe 检测 pin 存在即走 eBPF 事件路径（perf event ringbuf → Probe.Events）。
  4. 加载失败（权限/内核不兼容/pin 不存在）→ Guard 记 tamper.degraded 事件并切 PollWatcher（mtime+size 轮询），功能不中断，检测强度降级如实上报。
- 降级事件本身经 TAOOPS_SEC 通道回流中控，运维面板可见（A5）。

## 设计优化建议（界面对照发现，带方案上报，不自行改原型）

（收口时填写）

## 待真机验证清单

1. eBPF 实弹：云端 runner 按"真机加载路径"节步骤装配 tamper_probe.o，验证 pin 加载与事件回流。
2. URI 解码绕过修复实弹：OpenResty 装配后复放 `?id=1%20union%20select` 确认拦截。
3. Ingestor 自动订阅实弹：双节点上线后 SEC/WAF 事件免手工触发即回流。

## 已知边界（如实记录，非缺陷）

- eBPF 探针的内核态程序（C 源）属部署产物，本班交付加载契约与降级路径；探针 C 源随真机班落码。
- token 会话仍存中控内存（前班已知边界，会话持久化列后续班评估）。
- 非常规命令规则的"首次"口径按进程生命周期内存态（重启后重新学习），持久化基线列后续班评估。

## 验证回执（收口时填写）

## 修红记录（收口时填写）

## 制度对照三行（交活单，收口时定稿）

1. 本活上岗必读：tao-result-first（§12/§13/§16/§19）、tao-subagent-dispatch（§一3、§一13a、四·改动管理四纪律）、tao-code-standard 全文、tao-testing-standard 全文（含九.9 全面覆盖令）、tao-knowledge-hub（build-package/test-verify 分篇）、docs/protocol-contract.md、docs/breakpoint-m3-security.md、docs/breakpoint-m3-waf.md、PRD v1、全套原型——全部真读。
2. 落点：（收口时填写）
3. 对照结论：（收口时填写）
