# TaoOps 通信契约 v1（M2）

> 状态：基线冻结（2026-11-21，随 PRD v1.1 冻结）；M2 增补 TLS/TOFU、终端与日志流（2026-11-22）。本文档是 proto 的单一来源说明；改协议先改本文档再改 `src/proto/`。
> 方向：Agent 主动外连中控（产品不依赖云端，内网/离线友好）。

## 1. 传输层

| 通道 | 用途 | 说明 |
|---|---|---|
| gRPC (HTTP/2 + TLS) | 注册、心跳、指令下发、终端流、日志流 | 双向流 `AgentService` |
| WebSocket | 终端、实时日志流（中控 Web 控制台侧入口） | `/ws/agent/{node_id}/terminal`、`/ws/agent/{node_id}/logs?path=&tail=` |

- **TLS（M2 起默认）**：中控首次启动自签发长期证书（RSA-2048，有效期 10 年，含 SAN localhost/127.0.0.1），PFX 落数据目录（`TAOOPS_DATA_DIR`，默认 `/var/lib/taoops/server`），不依赖外部 CA——自托管无服务器定位。`TAOOPS_INSECURE=1` 仅保留给本地回环测试（明文 h2c）。
- **TOFU（Agent 侧）**：Agent 首次连接记录中控证书 SHA-256 指纹到固化文件（`TAOOPS_KNOWN_HOSTS`，默认 `~/.taoops/known_hosts`，格式 `host fingerprint` 每行一条）；此后每次连接校验，**不匹配立即拒绝连接**并输出 `fingerprint mismatch` 告警。中控换证书需管理员删除固化条目后重新确认。
- 心跳间隔：15 秒（`HeartbeatIntervalMs = 15000`，常量集中定义）。
- 断链重连：指数退避，初始 1s，倍增，上限 60s；成功注册后重置。

## 2. 服务定义（src/proto/agent.proto）

```protobuf
service AgentService {
  rpc Register(RegisterRequest) returns (RegisterResponse);
  rpc Heartbeat(stream HeartbeatMsg) returns (stream HeartbeatAck);
  rpc Dispatch(DispatchRequest) returns (DispatchResult);
  rpc CommandStream(stream DispatchResult) returns (stream DispatchRequest);
  // 终端双向流（M2）：Agent 发送终端输出、接收键盘输入；字节流，方向无关。
  rpc TermStream(stream TermData) returns (stream TermData);
  // 日志流（M2）：Agent 发送日志块，接收订阅控制（中控按控制台请求转发）。
  rpc LogStream(stream LogChunk) returns (stream LogControl);
}

message RegisterRequest {
  string agent_id  = 1;  // Agent 安装时生成的稳定 ID（UUID）
  string hostname  = 2;
  string os        = 3;  // 如 "ubuntu-22.04"
  string arch      = 4;  // amd64 / arm64
  string version   = 5;  // Agent 语义化版本
  repeated string tags = 6;
}
message RegisterResponse {
  string node_id   = 1;  // 中控分配的节点 ID
  NodeConfig config = 2; // 心跳间隔等配置
}

message HeartbeatMsg {
  string node_id = 1;
  int64  ts      = 2;  // Unix 毫秒
  double cpu     = 3;  // 0-100
  double mem     = 4;  // 已用百分比 0-100
  double disk    = 5;  // 根分区已用百分比
  double load    = 6;  // 1 分钟负载
}
message HeartbeatAck { int64 ts = 1; }

message DispatchRequest {
  string node_id = 1;
  string command = 2;  // M1 仅允许白名单命令（见 §4）
  repeated string args = 3;
  int32  timeout_ms = 4; // 默认 30000，上限 300000
  string request_id = 5; // 中控生成的唯一请求 ID，用于结果匹配
}
message DispatchResult {
  int32  exit_code = 1;
  string output    = 2; // stdout+stderr 合并，上限 1MB 截断
  string request_id = 3; // 对应 DispatchRequest.request_id
}

message TermData {
  string node_id   = 1; // Agent 侧填充
  string session_id = 2; // 终端会话 ID；Agent 生成，中控按其路由
  bytes  data      = 3; // 终端输入/输出字节
}

message LogChunk {
  string node_id = 1;
  bytes  data    = 2; // 新增日志字节
  int64  ts      = 3; // Unix 毫秒
}

message LogControl {
  string action     = 1; // subscribe | unsubscribe
  string path       = 2; // target=file 时为日志文件绝对路径；target=container 时为容器名
  int32  tail_lines = 3; // 订阅时回放的尾部行数，默认 0
  string target     = 4; // 订阅目标类型：file（默认）| container（FR-A11 容器日志回流）
}
```

## 3. 错误码

| 码 | 语义 | 处理 |
|---|---|---|
| OK(0) | 成功 | — |
| NOT_FOUND(5) | node_id 未注册 | Agent 重新 Register |
| INVALID_ARGUMENT(3) | 字段校验失败 | Agent 侧记录，不重试 |
| UNAVAILABLE(14) | 中控不可达 | 指数退避重连 |
| DEADLINE_EXCEEDED(4) | 指令超时 | 结果标记超时，进程被杀 |

## 4. 指令白名单与执行路径（M1）

- **Shell 白名单**：`echo`、`uname`、`df`、`free`、`uptime`。中控侧校验命令与参数（禁止 `;`、`|`、`&&`、反引号、`$(`）；白名单外一律 INVALID_ARGUMENT。
- **模块命令白名单**：`sysinfo.overview`、`site.manage`、`db.manage`、`files.manage`、`term.manage`、`cron.manage`、`env.manage`、`fw.manage`、`backup.manage`、`ftp.manage`、`sec.manage`、`docker.manage`（FR-A11）。模块命令的 `args` 采用 `key=value` 形式传入，Agent 侧还原为 `map[string]any`（无 `=` 的项忽略）。
- **容器日志回流（FR-A11）**：`LogControl.target=container` 时 `path` 携带容器名，Agent 侧经 docker.sock 追尾容器日志并推送增量；`docker logs` 进程退出即自动重启续订（断连自动重订）。`target` 缺省按 `file` 处理，向后兼容。
- **容器终端（FR-A11）**：`term.manage` 增 `op=attach_container`（args: `container=<容器名>`），会话创建与重连令牌语义不变；后续 `exec` 在该容器内执行（`docker exec <container> sh -c <command>`），session_id 路由不变。
- **执行路径**：中控 Dispatch 校验通过后，经 `CommandStream` 双向流转发到目标节点，由 Agent 侧统一分发器执行（模块命令走模块注册表，Shell 命令直接 exec、不经 shell），结果按 `request_id` 回传。中控在超时上限内等待；节点命令通道未连接返回 UNAVAILABLE。
- M2 起改为按节点授权的命令集。

## 5. 一致性约束

- `agent_id` 在中控侧幂等：重复注册返回同一 `node_id`。
- `CommandStream` 由 Agent 在注册成功后发起，请求头携带 `x-node-id`；中控按该头挂接节点命令通道，流断开即摘除并使该节点在途请求失败。
- 心跳流中断超过 3 个周期（45s）标记节点离线。
- 本契约、`src/proto/agent.proto`、两端实现三者必须一致；不一致按缺陷处理。

## 6. 面板 HTTP 下发桥（M3 企业层项5）

面板经 HTTP 触达节点，中控把请求转译为与 gRPC `Dispatch` 同一执行路径（经 `CommandStream` 转发），复用同一白名单校验（§4）与超时上限；每次下发进 FR-D4 审计哈希链（Action=`node.cmd`，Target=请求路径，Result=exit_code）。

| 端点 | 方法 | 说明 |
|---|---|---|
| `/api/nodes` | GET | 列出全部已知节点：`node_id`、`agent_id`、`hostname`、`last_seen_ms`、`registered_at_ms`、`command_connected`（命令通道是否在连）。 |
| `/api/nodes/{id}/cmd` | POST | 向节点下发指令。请求体 `{command, args?, timeout_ms?}`；`command` 必须命中 §4 白名单，`timeout_ms` 缺省 30000、上限 300000。响应为 `{exit_code, output, request_id}`。 |

- 鉴权：走 `/api` 统一 RBAC 中间件；只读角色（ReadOnly）写方法 403，Operator/Admin 可下发。
- 错误映射：节点未注册 → 404；命令不在白名单或参数含注入字符 → 400；节点命令通道未连接 → 503；下发超时 → 504。
- `request_id` 由中控生成（UUID），与 gRPC 路径同口径，用于结果匹配与审计溯源。

## 7. 节点间备份中转（FR-A13）

备份目标新增「其他受控节点」：源端 Agent 经中控中转（只过账不落盘、流式转发）把备份包传到目标节点 BackupDir。传输走自有 gRPC 通道，不经任何第三方；大文件中转复用 files 模块 4MB 分片语义（ChunkSize=4<<20、manifest 断点续传）。

- **新增 RPC**：`BackupRelay(stream RelayChunk) returns (stream RelayAck)`，由源端 Agent 发起。RelayChunk 字段：`relay_id`、`target_node_id`、`upload_id`、`name`（备份名）、`chunk_index`、`total_size`（备份包声明大小）、`chunk_count`、`file_sha256`（整包）、`chunk_sha256`（本分片）、`data`（≤4MB）、`eof`（末分片标记，请求整包校验）、`source`（源端原始备份来源路径，目标端 manifest 记录，跨节点还原用）。
- **中控转发**：每个分片经 `CommandStream` 以 `backup.manage op=relay_put` 下发到目标节点，`DispatchRequest.blob`（新增 `bytes` 字段）承载分片字节。单目标节点并发转发数=1；中控单流在途内存缓冲 ≤64MB（实现为单分片顺序转发，实际占用 4MB）。中控按 relay 记录中转流量（relayed_bytes），面板经 `GET /api/relay/stats` 查询（ReadOnly 及以上可读）。
- **目标端 ops（backup.manage）**：
  - `relay_begin`（args: `upload_id`、`name`、`total_size`、`chunk_count`、`file_sha256`）：磁盘预检——剩余空间 < total_size 即拒收并告警（非写满才失败）；建立装配会话目录与 manifest。
  - `relay_put`（args: `upload_id`、`chunk_index`、`chunk_sha256`；blob=分片字节）：分片级 SHA-256 校验，失败 nack 由源端重传该分片；写入 part 文件并更新 manifest（幂等，重复分片直接确认）。
  - `relay_status`（args: `upload_id`）：返回已收分片清单，供断点续传。
  - `relay_assemble`（args: `upload_id`）：全部分片到齐后拼装至 BackupDir（全量，不继承 FR-A9 增量），整包 SHA-256 仅作最终完整性确认；失败清理装配会话并告警。
  - `relay_abort`（args: `upload_id`）：清理装配会话。
- **源端 op（backup.manage）**：`send`（args: `file`、`target_node`、`name`）——异步发起（返回 `relay_id`，进度经 `send_status` 查询），流式读档、逐分片计算 SHA-256 推送；断线后重跑 `send` 即续传（目标端已收分片幂等跳过）。单 Agent 同一时刻至多一个在途 send。`send_status`（args: `relay_id`）返回状态/进度/错误。
- **信任模型**：默认中控可见备份明文（过账转发）；源端加密为后续可选项，本期不做。
- **告警接线（FR-A19）**：磁盘预检拒收、分片重传耗尽、整包校验失败 → warn 告警（source=backup）。
- **还原口径**：跨节点还原 = 持有备份的节点对目标节点执行 `send`，目标端 `restore` 本地执行；还原操作需 `confirm=true`，还原前自动快照（同 FR-A9）。
- **审计**：relay_begin/put/assemble/abort 经既有 `node.cmd` 审计哈希链留痕（口径同 §6）。

## 8. 一键迁移（FR-A14）

站点从源节点一键迁移到目标节点：源端打包（复用 FR-A9 全量备份逻辑）→ 经 §7 中转底座传输 → 目标端按迁移清单还原。新模块 `migrate.manage`（Agent 侧），传输完全复用 `backup.manage op=send/send_status` 与 §7 中转通道，不新增 RPC。

- **源端 ops（migrate.manage）**：
  - `precheck`（args: `source_path`、`target_path`、`force?`）：生成预检报告——源/目标路径均须绝对路径且 `Clean` 后不含 `..`；统计文件数与总大小；采集源端 Agent 版本。任一路径校验失败直接拒绝（ExitCode 2）；环境版本差异进报告（源端版本 vs 目标端版本在 restore 时比对），`force=true` 时报告标注 `forced` 且放行（留审计，走 §6 哈希链）。
  - `pack`（args: `source_path`、`target_path`、`name`）：全量打包源路径至 BackupDir（复用 backup 模块 createArchive），并写迁移清单 `<archive>.migrate.json`：`archive`（档案路径）、`source_path`、`target_path`、`source_version`（Agent 版本）、`created`、`files`（文件数）、`total_size`。清单随后作为 restore 参数经中控下发（JSON args，走普通命令通道）。
- **目标端 op（migrate.manage）**：`restore`（args: `file`、`mapping`（JSON：`source_path`/`target_path`）、`source_version`、`confirm`、`force?`）：
  1. `file` 必须位于 BackupDir 内（绝对路径校验，同 §7 send 口径）。
  2. `target_path` 绝对路径、`Clean` 后不含 `..`，且逐级校验已存在路径组件非符号链接（防符号链接逃逸）。
  3. 版本比对：`source_version` ≠ 本端 Agent 版本 → 默认拒绝（ExitCode 2），`force=true` 跳过且报告标注 `forced`。
  4. `confirm=true` 必须（同 FR-A9 口径），缺失返回提示不执行。
  5. 还原前对 `target_path` 现状自动快照（同 FR-A9 回滚快照口径；目录不存在则跳过快照）。
  6. 解压档案至 `target_path`（防路径穿越同 FR-A9）。
  7. 迁移报告：`restored`（成功文件数）、`skipped`（快照保留的原文件说明）、`failed`（失败明细，含原因）；报告随 `data` 返回供面板展示。
- **鉴权（MigratePolicy，中控）**：`precheck`/`pack` = Operator 及以上；`restore` = Admin（覆盖目标路径写操作）；未知操作默认 Admin（默认拒绝）。专用端点 `POST /api/nodes/{id}/migrate`，经下发桥审计留痕（口径同 §6）。
- **传输编排（面板）**：precheck（源节点）→ pack（源节点）→ `backup.manage op=send`（源节点，轮询 `send_status`）→ `migrate.manage op=restore`（目标节点，confirm=true）。四步均走 §6 下发桥，逐条审计。

## 9. 负载均衡 upstream 管理（FR-A18）

新模块 `lb.manage`（Agent 侧）：管理 Nginx upstream 配置片段（增删后端、权重、健康检查、备用标记），生成独立片段落盘；站点引用由用户在 FR-A2 站点管理内自行配置，本期不做站点联动改造。

- **配置落点**：`<confDir>/lb/<name>.conf`（confDir 为 Agent 站点配置目录）；片段仅含 `upstream <name> { ... }` 块，server 行形如 `server <host>:<port> weight=<w> [max_fails=3 fail_timeout=30s] [down] [backup];`。
- **ops**：
  - `list`（args: `name?`）：列出全部 upstream 组与后端明细（host、port、weight、backup、down、健康状态）。
  - `add`（args: `name`、`host`、`port`、`weight?`、`backup?`、`health_mode`（tcp|http）、`health_path?`）：新增后端到指定组（组不存在则创建）；host 校验正则 `^[a-zA-Z0-9._-]+$`（IPv6 括号形式单独口径，本期拒绝并提示）；端口 1-65535；权重 1-100 整数；health_mode 必填二选一，http 时 health_path 必填且以 `/` 开头。
  - `remove`（args: `name`、`host`、`port`）：从组中移除后端；组空则删除整个片段文件。
  - `drain`（args: `name`、`host`、`port`）：标记 down（摘除，保留配置）。
  - `undrain`（args: `name`、`host`、`port`）：取消 down 标记。
  - `health`（args: `name?`）：对组内后端执行健康检查（TCP 拨号或 HTTP GET），连续 3 次失败判下线（down 状态落盘），检查间隔 30s 由面板/定时触发；返回各后端健康状态。
- **落盘校验（验收口径）**：片段写入前先写临时文件并执行 `nginx -t`（若 nginx 可用；不可用时跳过并标注 skipped），失败不生效并回滚（删除临时文件、保留原片段）；变更（add/remove/drain/undrain）逐条留审计（口径同 fw 模块审计文件）。
- **鉴权（LbPolicy，中控）**：`list`/`health` = ReadOnly 及以上；`add`/`remove`/`drain`/`undrain` = Operator 及以上；未知操作默认 Admin（默认拒绝）。专用端点 `POST /api/nodes/{id}/lb`，经下发桥审计留痕（口径同 §6）。
- **审计**：全部变更 ops 经既有 `node.cmd` 审计哈希链留痕；Agent 侧审计文件记录每次片段变更前后摘要。

## 11. 自定义告警推送（FR-A20）

告警触发时向用户自填 webhook（钉钉/飞书/企业微信机器人或任意 HTTP 端点）POST JSON。纯中控侧能力，不涉节点下发，不新增 RPC；平台侧不建任何推送服务。

- **渠道配置（dataDir/push/channels.json）**：`id`、`name`、`url`、`enabled`、`created_ms`。用户自填 URL，仅支持 http(s)。
- **SSRF 防护（SsrfGuard）**：目标域名解析的全部 A/AAAA 记录均不得命中内网段——10/8、172.16/12、192.168/16、127/8、169.254/16、0.0.0.0、::1、fc00::/7、fe80::/10；IP 字面量直接判定。SSRF 白名单（dataDir/push/ssrf-whitelist.json，条目为主机名精确匹配或 CIDR 网段）由 Admin 面板配置、**默认为空**、变更进审计哈希链（Action=`push.whitelist`）。HTTP 客户端禁用自动重定向（AllowAutoRedirect=false）。
- **推送触发（AlertCenter 接线）**：`Raise` 产生 critical 级别告警时异步触发（fire-and-forget，不阻塞告警入库），向全部启用渠道推送；JSON 模板含告警字段（id/source/level/node_id/detail/ts）。
- **重试**：失败重试 3 次，退避间隔 1s/2s/4s；每次尝试独立留痕。
- **推送日志（dataDir/push/log.jsonl）**：记录时间/渠道/URL/状态码/结果/尝试序号；保留策略同告警（默认 90 天）。
- **端点（全部 Admin）**：
  - `GET /api/push/channels`：渠道列表。
  - `POST /api/push/channels`：新增/更新渠道（URL 过 SsrfGuard 校验，内网地址白名单外拒绝）；审计 Action=`push.channel`。
  - `DELETE /api/push/channels/{id}`：删除渠道；审计。
  - `GET|PUT /api/push/whitelist`：SSRF 白名单查询/变更（PUT 整体替换，逐条进审计）。
  - `POST /api/push/test`：向指定渠道发送测试载荷，返回逐次尝试结果。
  - `GET /api/push/log`：推送日志查询。

## 10. 两步验证（FR-A16）

面板登录二次校验（TOTP，RFC 6238），纯中控侧（auth 模块扩展），Agent 无涉及。全程本地算法（HMAC-SHA1 手写流程），无外部依赖、不引第三方包。

- **数据落点**：`<dataDir>/auth/totp/<username>.json`（文件权限 600）：`secret`（Base32 密钥）、`enabled`（绑定确认后 true）、`recovery_codes`（哈希数组，PBKDF2 同口径）、`created_ms`。密钥与恢复码哈希不进审计、不进日志、不回显（绑定/重生成时一次性展示）。
- **绑定流程**：`POST /api/auth/totp/setup`（需登录 + 密码复验 `password` 字段）→ 生成新密钥与 Base32、otpauth URI（前端渲染二维码或展示密钥）→ 用户提交 6 位码 `POST /api/auth/totp/enable`（需密码复验）→ 校验通过后 `enabled=true` 并返回 10 枚恢复码（明文仅此一次）。
- **登录二次校验**：`POST /api/auth/login` 密码正确且该账号已启用 TOTP → 不发 token，返回 `{totp_required: true}`（HTTP 200）；前端补 `POST /api/auth/totp/verify`（`username`、`code`——6 位 TOTP 或恢复码）→ 校验通过发正常会话。校验失败计入与密码同一失败锁定通道（连续 5 次锁定 5 分钟，走既有 AuthService 锁定口径）。
- **时钟窗**：±1 个 30s 周期（当前周期及前后各一，共 3 个候选码任一匹配即通过）；拒绝已使用的上一周期码（防重放，记录 last_used_step）。
- **恢复码**：10 枚，8 位 Base32 字符；仅存 PBKDF2 哈希；用后即焚（单次有效）；`POST /api/auth/totp/recovery/regenerate`（需登录 + 密码复验）重新生成 10 枚，旧码全部失效。
- **端点**：`/api/auth/totp/setup`、`/api/auth/totp/enable`、`/api/auth/totp/verify`、`/api/auth/totp/status`（GET，是否已启用）、`/api/auth/totp/recovery/regenerate`。全部走既有 Bearer/RBAC 中间件；setup/enable/regenerate 需密码复验（防会话劫持改配置）。
- **前端**：登录页在 `totp_required` 后展示 6 位码输入；设置页（权限与审计内新页签）提供绑定（二维码/密钥展示 + 确认码）与恢复码重生成。
- **审计**：setup/enable/verify 成败/regenerate 均落审计（Action=login 或 totp.*，Result=ok/invalid），密钥与恢复码明文不落审计。

## 12. 日志清理（FR-A17）

新模块 `logclean.manage`（Agent 侧）：按路径/大小/年龄规则定时清理日志文件，命中文件先压缩归档（默认保留 7 天）再删，白名单目录绝对保护。

- **规则落点**：`<dataDir>/logclean/rules.json`：`id`、`path`（绝对路径，前缀匹配语义）、`max_size_mb`（0=不限）、`max_age_days`（0=不限，mtime 按天取整）、`enabled`、`created_ms`。二者至少一项大于 0 才算有效规则。
- **内置白名单（绝对保护，不可配置移除）**：`/etc`、`/usr`、`/bin`、`<dataDir>`（运行时解析为绝对路径后比较）。清理目标命中白名单（规则路径或待删文件路径为白名单目录本身或其子路径）直接拒绝并告警（接 FR-A19 事件上报，级别 warn）。
- **路径校验（验收口径）**：仅接受绝对路径；拒绝根路径 `/` 与过宽通配（路径含 `*` 时，通配段后必须仍有具体目录层级，`/*`、`/var/*` 形式拒绝）；路径匹配语义＝前缀匹配（规则 path 为待清理文件的前缀）；规范化后校验，禁止 `..` 与符号链接逃逸（清理时对每个命中文件取 EvalSymlinks 后再判白名单与前缀）。
- **ops**：
  - `list`：列出全部规则与最近一次清理留痕摘要。
  - `add`（args: `path`、`max_size_mb?`、`max_age_days?`）：新增规则；路径校验 + 白名单拒绝；max_size_mb 与 max_age_days 至少一项 >0。
  - `remove`（args: `id`）：删除规则。
  - `run`（args: `id?`，缺省跑全部启用规则）：执行清理——按规则匹配文件（前缀匹配 + mtime 按天取整判龄 + 大小阈值），命中文件先 gzip 压缩归档到 `<dataDir>/logclean/archive/<yyyymmdd>/`（原文件删除），再逐条留痕（路径/大小/归档位置）到 `<dataDir>/logclean/cleaned.jsonl`；归档目录按天过期自动清理（默认保留 7 天）。
- **鉴权（LogCleanPolicy，中控）**：`list` = ReadOnly 及以上；`add`/`remove`/`run` = Operator 及以上；未知操作默认 Admin（默认拒绝）。专用端点 `POST /api/nodes/{id}/logclean`，经下发桥审计留痕（口径同 §6）。
- **审计**：add/remove/run 全部经既有 `node.cmd` 审计哈希链留痕；Agent 侧 cleaned.jsonl 记录每次清理明细（路径/大小/归档位置/时间）。

## 13. MySQL 主从复制（FR-A15）

`db.manage` 扩展四个复制 ops（仅 MySQL；PostgreSQL 不在本期范围）。Agent 侧经 mysql CLI 执行，凭据不落盘、不回显。

- **ops**：
  - `master_grant`（args: `replica_host`）：主库侧创建复制账号——用户名 `repl_<8位随机>`、密码 24 位强随机（crypto/rand，大小写+数字+符号），`CREATE USER ... IDENTIFIED BY ...` + `GRANT REPLICATION SLAVE ON *.* TO ...`；密码仅在本次 Output 展示一次，后续查询不回显；重复调用生成新账号（旧账号不自动清理，由用户自行 DROP）。
  - `replica_config`（args: `master_host`、`master_port?`（默认 3306）、`master_user`、`master_password`、`master_log_file?`、`master_log_pos?`）：从库侧执行 `STOP SLAVE` → `CHANGE MASTER TO MASTER_HOST=..., MASTER_PORT=..., MASTER_USER=..., MASTER_PASSWORD=... [, MASTER_LOG_FILE=..., MASTER_LOG_POS=...]` → `START SLAVE`；变更前对 `/etc/my.cnf`（或 `/etc/mysql/my.cnf`，存在哪个备份哪个）做时间戳备份（口径同 FR-A2：备份目录 + `.<yyyymmddhhmmss>.bak` 后缀）；Output 中所有凭据字段统一脱敏为 `***`。
  - `replica_status`：`SHOW SLAVE STATUS\G` 解析 `Slave_IO_Running`、`Slave_SQL_Running`、`Seconds_Behind_Master`、`Master_Host`、`Last_Error`；Output 为 JSON（含上述字段；未配置复制时 `configured=false`）。
  - `replica_reset`：`STOP SLAVE` → `RESET SLAVE ALL`（断链重置，清除复制配置）。
- **凭据脱敏（验收口径）**：中控下发桥审计事件不记录 args（结构上不落凭据）；Agent 侧 replica_config 的 Output 与任何错误信息统一脱敏（`Redact` 助手：值中含密码字段的 key=value 对替换为 `***`）；master_grant 密码仅创建时 Output 展示一次。
- **复制中断巡检（中控侧 ReplicationMonitor）**：每 60s 对全部命令通道在线节点下发 `db.manage op=replica_status`；IO 或 SQL 线程非 Yes → 告警 `replication` 级别 warn（边缘触发，恢复后复位）；`Seconds_Behind_Master` > 阈值（env `TAOOPS_REPLICA_LAG_THRESHOLD`，默认 300）→ 告警 warn（同边缘触发口径）。告警接 FR-A19 告警中心。
- **鉴权（DbPolicy，中控）**：`replica_status` = ReadOnly 及以上；`master_grant`/`replica_config`/`replica_reset` = Admin（凭据与复制拓扑变更）；其余既有 db ops 口径不变。新增专用端点 `POST /api/nodes/{id}/db`，经下发桥审计留痕（口径同 §6）。
- **审计**：master_grant/replica_config/replica_reset 经既有 `node.cmd` 审计哈希链留痕（不含 args）；Agent 侧无新增审计文件（mysql 操作经 CLI，凭据不落盘）。

## 14. SSL 证书自动续期（FR-A21）

新模块 `ssl.manage`（Agent 侧）：Let's Encrypt ACME（HTTP-01）签发与自动续期、已有证书上传管理、到期提醒（接 FR-A19，默认级别 warn）、自签证书路径保留（离线场景）。

- **数据落点**：`<dataDir>/ssl/certs.json`（证书清单：domain、cert_path、key_path、issuer（letsencrypt|uploaded|selfsigned）、not_after、updated_ms）；私钥落盘权限 600（upload 与 selfsign 写入时显式设置；letsencrypt 由 certbot 落盘后模块校验并收紧到 600）。
- **ops**：
  - `list`：清单 JSON（domain/cert_path/issuer/not_after）。
  - `precheck`（args: `domain`）：80 端口可达性预检（拨号 `domain:80`，超时 5s）；失败返回明确报错与指引文案（检查域名解析、防火墙/安全组放行 80、站点 80 端口公网可达）。
  - `issue`（args: `domain`、`email`、`webroot`）：先 precheck，再经 certbot（`certonly --webroot -w <webroot> -d <domain> --non-interactive --agree-tos -m <email>`）签发；certbot 不存在时返回安装指引；签发后登记清单并校验私钥权限 600。
  - `renew`（args: `domain`）：先 precheck，再 `certbot renew --cert-name <domain> --non-interactive`（到期前 30 天内才实际执行，未到期返回 skipped）；失败返回明确报错（面板侧产生续期失败告警）。
  - `upload`（args: `domain`、`cert_pem`、`key_pem`）：已有证书上传管理——落盘 `<dataDir>/ssl/<domain>.crt` 与 `.key`（600），解析 not_after 登记清单。
  - `selfsign`（args: `domain`、`days?`（默认 3650））：Go crypto/x509 自签证书（离线场景保留路径），私钥 600，登记清单。
- **自动续期（中控侧 SslMonitor）**：每 24h 对全部在线节点下发 `ssl.manage op=list`；证书 not_after ≤30 天 → 先触发 `op=renew`，续期失败或仍到期 → 告警 `ssl` 级别 warn（边缘触发，续期成功或证书更新后复位）；>30 天不动作。
- **鉴权（SslPolicy，中控）**：`list`/`precheck` = ReadOnly 及以上；`issue`/`renew`/`upload`/`selfsign` = Operator 及以上；未知操作默认 Admin（默认拒绝）。专用端点 `POST /api/nodes/{id}/ssl`，经下发桥审计留痕（口径同 §6）。
- **审计**：issue/renew/upload/selfsign 经既有 `node.cmd` 审计哈希链留痕；私钥内容不进审计、不进日志。

## 15. K8s 集成（FR-A28）

中控侧新模块 `k8s`：对接用户已有集群（不装集群，对齐 1Panel 边界），三层全做——只读接入+告警、基础操作、一键部署延伸到集群。中控经官方 KubernetesClient 直连 API Server，Agent 不参与。

- **集群接入**：面板添加集群（kubeconfig 粘贴或上传），中控保存 `<dataDir>/k8s/clusters/<name>.kubeconfig`（落盘 600）；连接测试（version 探测）通过才算接入成功。集群元数据（名称、接入时间、server 地址、版本）登记 `<dataDir>/k8s/clusters.json`；删除集群需 `confirm=true`，同时删除 kubeconfig 文件。
- **安全红线**：kubeconfig 只存中控、落盘 600；审计脱敏（不落凭据内容、不落 kubeconfig 原文）；绝不下发节点 Agent。
- **只读层**：节点列表、工作负载（Deployment/StatefulSet/DaemonSet）、Pod 状态、事件、Pod 日志（读操作但进审计留痕——安全评审保留意见③）。
- **告警（K8sMonitor）**：每 60s 对全部集群巡检 Pod 健康；CrashLoopBackOff / ImagePullBackOff / 重启次数激增（≥5 次/10min）产生 `k8s` 级别 warn 告警（接 FR-A19，边缘触发，恢复复位）；巡检失败静默（监控自身降级不产生告警噪音，口径同 FR-A23）。
- **操作层**：Deployment 扩缩容（replicas）、重启（rollout restart，patch restartAt 注解）、滚动更新进度（status 观察）；变更操作限 Admin，审计留痕。
- **部署层**：FR-A24 模板渲染为 K8s 清单（Deployment+Service，带 PVC 预留字段），提交到指定 namespace；与单机模板共用模板库；镜像引用走 FR-A11 镜像正则。
- **RBAC（K8sPolicy）**：集群管理（增删）=Admin；查看（nodes/workloads/pods/events/logs）=ReadOnly 及以上；资源操作（scale/restart/deploy/remove）=Admin；未知操作默认 Admin（默认拒绝）。专用端点 `POST /api/k8s/{cluster}/...` 与 `GET /api/k8s/{cluster}/...`，经统一鉴权与审计。
- **边界**：不装集群、不做多租户、不做 Helm 生态（清单直渲染）；K8s 版本兼容按客户端库支持矩阵标注。
