# M2 断点档案（安全班 · TLS/TOFU + WebSocket 流 + 跨节点 + FR-A8）

> 更新：2026-11-22 03:40 · M2 安全班（重开班，前一班死线 2026-11-21 20:00 超 7h 无交付后按零停摆制度重开）

## 状态：M2 全部完成，统一验证全绿，已提交

## 交付内容（对照四项验收）

1. **TLS+TOFU 真加密替换明文回环** ✅
   - 中控：`server/CertManager.cs` 首启自签 RSA-2048/10 年（SAN localhost/127.0.0.1/::1），PFX 落 `TAOOPS_DATA_DIR`（默认 /var/lib/taoops/server），重启复用同指纹；`Program.cs` 默认 TLS ListenAnyIP(TAOOPS_PORT||5241, Http2+Http1)+UseHttps；`TAOOPS_INSECURE=1` 明文（M1 兼容分支保留）
   - Agent：`internal/agent/pinning.go` PinStore TOFU 固化 SHA-256 指纹到 `TAOOPS_KNOWN_HOSTS`（默认 ~/.taoops/known_hosts），不匹配报 `fingerprint mismatch` 拒连；`cmd/dispatch` 同口径
2. **WebSocket 终端/日志实时流** ✅
   - `NodeStreamHub.cs`（term 会话/log 订阅广播）+ `WsBridge.cs`（`/ws/agent/{node}/terminal`、`/logs?path=&tail=`）+ `AgentService.cs` TermStream/LogStream 双泵；proto 新增 TermStream/LogStream（只增不改）
   - Agent 侧 `term.go`（bash -i）/`logs.go`（tailFile 500ms 轮询+轮转处理+subscribe 控制）
   - 探针 `tools/wsprobe`（term/logs 两模式，已入 TaoOps.slnx）
3. **跨节点真实下发** ✅ E2E 双节点（agent-001/002）各自 dispatch echo 断言各自输出
4. **FR-A8 FTP 模块** ✅ `internal/modules/ftp`（adduser/deluser/grant/revoke/list/test，JSON 存口令哈希，防路径穿越），E2E 真实下发 adduser→list→持久化断言

## 验证回执（数字）

- Go：`go build/vet` 干净，`go test ./...` **15 包 ok**（16 目录，1 无测试文件）
- .NET：`dotnet build TaoOps.slnx -c Release` **0 错 0 警**；`dotnet test` **24/24 PASS**（M1 15 + 新增 CertManager 2 + NodeStreamHub 7）
- E2E：`docs/e2e_loop.sh`（M1 回归）**7/7 PASS**；`docs/e2e_m2.sh` **9/9 PASS**（证书落盘→TOFU 首连→二连校验→WS 终端 echo→WS 日志流→双节点下发→ftp.manage→白名单回归→换证书指纹不匹配拒绝）

## 踩坑记录（复用价值）

1. `X509CertificateLoader` 正确方法名是 `LoadPkcs12(byte[], string)`，不是 `LoadPkcs12FromBytes`
2. **`go run` 杀包装进程会留孤儿**：E2E 里 kill `go run` 的 PID，agent 二进制仍在跑并占着 log 流，新 Agent 撞 `AlreadyExists` 死循环。改预编译 `go build -o` 直跑
3. dispatch 工具 TOFU 指纹默认固化 `~/.taoops/known_hosts`，跨轮换证书必撞 mismatch（机制正确）；E2E 里给 dispatch 单独 `TAOOPS_KNOWN_HOSTS`
4. Agent 被 kill 后中控摘流有延迟，立即重连会撞 `AlreadyExists`；E2E 重启前 sleep 2
5. M1 E2E 脚本两处 M2 适配：中控起明文须显式 `TAOOPS_INSECURE=1`；根路径文案断言放宽为 "TaoOps Server"
6. `AgentService` 构造函数新增 NodeStreamHub 参数，测试 `Create()` 已补

## 制度对照

- tao-result-first §12/13（基线不倒退、死线交付）：Go 15 包全绿、.NET 0错0警 24 测试全绿、M1 回归 7/7 + M2 9/9，全部高于 M1 基线（15/15、7/7）
- tao-result-first §16/19（统一验证、产出自检）：四类验证全部真实跑过并贴数字；E2E 断言逐项可复核（/tmp/taoops-e2e-m2 留日志）
- tao-subagent-dispatch §一3/§一13a/四（改动管理）：一线一提交，proto 只增不改，已发布产物未动
- tao-code-standard / tao-testing-standard：新码带中文契约注释；单测覆盖 CertManager/NodeStreamHub/ftp/pinning；沙箱无 crontab/vsftpd 沿 M1 先例以单测覆盖

## 还差什么 / 下一步（M3）

- WAF/OpenResty（M3 范围，未动）
- 本班无遗留断点；若后续班接手：`bash docs/e2e_m2.sh` 应 9/9 PASS 为入场基线
