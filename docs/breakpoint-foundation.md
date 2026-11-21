# M1 通信地基 · 断点与交付记录（2026-11-21）

## 交付状态：完成（全绿）

## 做到了哪
- 契约文档 `src/docs/protocol-contract.md`：消息定义、错误码、重连策略、指令白名单，与 proto/两端实现一致。
- `src/proto/agent.proto`：Register / Heartbeat(双向流) / Dispatch，Go 侧代码已生成（`agent/pb/`），.NET 侧 Grpc.Tools 构建期生成。
- 中控 `src/server/`（.NET 10 / ASP.NET Core / h2c）：NodeRegistry（幂等注册）、AgentService（白名单校验+本机回环执行）、LocalExec。
- Agent `src/agent/`（Go 1.23）：注册→心跳流→指数退避重连（1s→60s）；指标采集 /proc；TAOOPS_INSECURE=1 明文回环，TLS+TOFU 指纹 M2 接入（worker.go dial 处 TODO 已注明）。
- E2E 回环 `src/docs/e2e_loop.sh`：起中控→起 Agent→断言注册+心跳→Dispatch(echo)→结果回传，4/4 PASS。

## 验证回执
- dotnet build（TaoOps.slnx，Release）：0 错 0 警。
- dotnet test：9/9 通过。
- go build ./... 干净；go vet 干净；go test 12 包全 ok，0 FAIL。
- E2E：PASS（见上）。
- git 基线：`91b6c07`（M1: E2E loop green）。

## 还差什么（移交 M2/下一班）
- TLS 真证书 + TOFU 指纹固化链路（当前 M1 回环用明文开关，生产路径代码位已留）。
- WebSocket 通道（终端/日志流）——契约已预留路径。
- Dispatch 跨节点真实下发（当前中控本机回环执行；Agent 指令通道 M2 接入）。
- M1 联调需落实 Linux 测试目标机（goal.md 已记）。

## 制度对照三行（交活单）
1. 必读：tao-result-first（§12/§13/§16/§19）、tao-subagent-dispatch（§一13a、四·三道闸与四纪律）、tao-code-standard 全文、tao-testing-standard 全文、tao-knowledge-hub（env-roadmap 1.4 dotnet 恢复口径）。
2. 落点：开工先 git init 落基线（四·改动管理）；断言先行——单测先于实现定稿（编码规范一.10）；统一验证——.NET 与 Go 全量构建+测试在批次末一次跑（总纲§16）；环境首检——dotnet10/go/protoc 先核验再动工（调度§一11）。
3. 对照结论：无偏差。Go 首轮编译 2 处红（未用 import、流 API 误用）当场修，修后统一验证全绿；未压警告、未跳关。
