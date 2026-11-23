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
  string path       = 2; // 日志文件绝对路径
  int32  tail_lines = 3; // 订阅时回放的尾部行数，默认 0
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
- **模块命令白名单**：`sysinfo.overview`、`site.manage`、`db.manage`、`files.manage`、`term.manage`、`cron.manage`、`env.manage`、`fw.manage`、`backup.manage`、`ftp.manage`（M2，FR-A8）。模块命令的 `args` 采用 `key=value` 形式传入，Agent 侧还原为 `map[string]any`（无 `=` 的项忽略）。
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
