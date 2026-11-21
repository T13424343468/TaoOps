# TaoOps 通信契约 v1（M1）

> 状态：基线冻结（2026-11-21，随 PRD v1.1 冻结）。本文档是 proto 的单一来源说明；改协议先改本文档再改 `src/proto/`。
> 方向：Agent 主动外连中控（产品不依赖云端，内网/离线友好）。

## 1. 传输层

| 通道 | 用途 | 说明 |
|---|---|---|
| gRPC (HTTP/2 + TLS) | 注册、心跳、指令下发 | 双向流 `AgentService` |
| WebSocket (wss) | 终端、实时日志流 | M1 仅预留路径 `/ws/agent/{node_id}`，M2 启用 |

- TLS：中控自签证书；Agent 首次连接记录证书指纹（SHA-256），人工确认后固化（TOFU）。指纹不匹配即拒绝连接并告警。
- 心跳间隔：15 秒（`HeartbeatIntervalMs = 15000`，常量集中定义）。
- 断链重连：指数退避，初始 1s，倍增，上限 60s；成功注册后重置。

## 2. 服务定义（src/proto/agent.proto）

```protobuf
service AgentService {
  rpc Register(RegisterRequest) returns (RegisterResponse);
  rpc Heartbeat(stream HeartbeatMsg) returns (stream HeartbeatAck);
  rpc Dispatch(DispatchRequest) returns (DispatchResult);
  rpc CommandStream(stream DispatchResult) returns (stream DispatchRequest);
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
- **模块命令白名单**：`sysinfo.overview`、`site.manage`、`db.manage`、`files.manage`、`term.manage`、`cron.manage`、`env.manage`、`fw.manage`、`backup.manage`。模块命令的 `args` 采用 `key=value` 形式传入，Agent 侧还原为 `map[string]any`（无 `=` 的项忽略）。
- **执行路径**：中控 Dispatch 校验通过后，经 `CommandStream` 双向流转发到目标节点，由 Agent 侧统一分发器执行（模块命令走模块注册表，Shell 命令直接 exec、不经 shell），结果按 `request_id` 回传。中控在超时上限内等待；节点命令通道未连接返回 UNAVAILABLE。
- M2 起改为按节点授权的命令集。

## 5. 一致性约束

- `agent_id` 在中控侧幂等：重复注册返回同一 `node_id`。
- `CommandStream` 由 Agent 在注册成功后发起，请求头携带 `x-node-id`；中控按该头挂接节点命令通道，流断开即摘除并使该节点在途请求失败。
- 心跳流中断超过 3 个周期（45s）标记节点离线。
- 本契约、`src/proto/agent.proto`、两端实现三者必须一致；不一致按缺陷处理。
