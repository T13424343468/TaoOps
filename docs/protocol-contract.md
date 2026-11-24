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
