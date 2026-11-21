# M1 联调 · 断点与交付记录（2026-11-21）

## 交付状态：完成（全绿）

## 做到了哪
- **联调打通**：新增 `CommandStream` 双向流（契约 §2/§4/§5 同步更新，proto 重生成）。中控 Dispatch 校验后经 `NodeCommandHub`（src/server/NodeCommandHub.cs）转发到目标节点；Agent 侧统一分发器（src/agent/internal/dispatch）执行：模块命令走模块注册表（args 按 key=value 还原为 map），Shell 白名单直接 exec 不经 shell（注入字符惰性化）。结果按 request_id 匹配回传；节点命令通道未连接返回 UNAVAILABLE，Agent 静默超时返回 DEADLINE_EXCEEDED。
- **E2E 扩展**：src/docs/e2e_loop.sh 共 7 项断言全 PASS——服务起、注册+心跳、命令流挂接、shell echo、真实模块 sysinfo.overview、真实模块 backup.manage op=list、shell df。cron 断言因沙箱无 crontab 由单测覆盖（cron.manage 单测全绿）。
- **FR-A9 备份还原（P0）落码**：src/agent/internal/modules/backup——op=create（全量/增量，增量按 size+mtime 跳过未变文件）、op=list、op=restore（无 confirm 拒绝并提示；还原前自动做回滚快照）、op=rollback；防路径穿越（拒绝绝对路径与 ..）；manifest sidecar JSON。
- **替换回环执行器**：LocalExec.cs 删除；中控不再本机执行，全部经 Agent 通道。

## 验证回执
- dotnet build（TaoOps.slnx，Release）：0 错 0 警。
- dotnet test：15/15 通过（新增：模块命令白名单、注入字符拒绝、节点未连接 UNAVAILABLE、Agent 静默 DEADLINE_EXCEEDED、命令流未知节点/缺头 NotFound、经 hub 端到端回传）。
- go build / go vet：干净；go test：14 包全 ok，0 FAIL（新增 dispatch 9 用例、backup 8 用例）。
- E2E：7/7 PASS。
- git 基线：见仓库最新提交（M1: integration + FR-A9）。

## 还差什么（移交 M2/下一班）
- TLS 真证书 + TOFU 指纹固化链路（M1 回环仍用 TAOOPS_INSECURE=1 明文开关）。
- WebSocket 通道（终端/日志流）——契约已预留路径。
- M1 真机联调目标机未落实（goal.md 已记）；沙箱 E2E 为同机回环。
- FR-A8（FTP，P1）仍未落码。

## 制度对照三行（交活单）
1. 必读：tao-result-first（§12/§13/§16/§19）、tao-subagent-dispatch（§一13a、四·改动三道闸）、tao-code-standard 全文、tao-testing-standard 全文、tao-knowledge-hub（Go/dotnet 构建分篇）。
2. 落点：契约先行——改协议先改文档再改 proto（契约文档头部约定）；断言先行——hub 转发语义先定测试桩再实现；统一验证——.NET 与 Go 全量构建+测试批次末一次跑；环境首检——dotnet10/go/protoc 先核验（protoc 27.3 + protoc-gen-go 1.35.2 在 /root/workspace/.local）。
3. 对照结论：无偏差。修红 5 处当场处理（Channel 缺 using、测试桩把"暂无结果"当流结束、Task 误 Dispose、回滚快照秒级时间戳自我覆盖、恶意归档测试字节数），修后统一验证全绿；未压警告、未跳关。
