#!/usr/bin/env bash
# TaoOps M1 端到端回环：起中控 → 起 Agent → 断言注册+心跳 → 中控下发指令 → 结果回传
# 用法：bash e2e_loop.sh ；输出 PASS/FAIL，退出码 0/1
set -u
export PATH=/root/workspace/.local/go/bin:/root/workspace/.local/dotnet10:$PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1
SRC=/root/workspace/projects/2377946/src
PORT=5241
LOG=/tmp/taoops-e2e
mkdir -p $LOG

cleanup() { [ -n "${SERVER_PID:-}" ] && kill $SERVER_PID 2>/dev/null; [ -n "${AGENT_PID:-}" ] && kill $AGENT_PID 2>/dev/null; }
trap cleanup EXIT

# 1. 起中控（gRPC 需 HTTP/2 明文：Kestrel 开 h2c）
cd $SRC/server
export ASPNETCORE_Kestrel__EndpointDefaults__Protocols="Http2"
dotnet run -c Release --no-build --urls "http://127.0.0.1:$PORT" > $LOG/server.log 2>&1 &
SERVER_PID=$!
for i in $(seq 1 30); do
  curl -s --http2-prior-knowledge "http://127.0.0.1:$PORT/" >/dev/null 2>&1 && break
  sleep 0.5
done
curl -s --http2-prior-knowledge "http://127.0.0.1:$PORT/" 2>/dev/null | grep -q "TaoOps Server M1" || { echo "FAIL: server not up"; cat $LOG/server.log; exit 1; }
echo "[1/4] server up"

# 2. 起 Agent（gRPC 明文 http）
cd $SRC/agent
TAOOPS_SERVER="127.0.0.1:$PORT" TAOOPS_AGENT_ID=e2e-agent-001 TAOOPS_INSECURE=1 go run ./cmd/agent > $LOG/agent.log 2>&1 &
AGENT_PID=$!

# 3. 断言注册与心跳（轮询 server 日志）
REGISTERED=0; HEARTBEAT=0
for i in $(seq 1 40); do
  grep -q "Agent registered: agent_id=e2e-agent-001" $LOG/server.log && REGISTERED=1
  grep -q "registered as node" $LOG/agent.log && HEARTBEAT=1
  [ $REGISTERED -eq 1 ] && [ $HEARTBEAT -eq 1 ] && break
  sleep 0.5
done
[ $REGISTERED -eq 1 ] && [ $HEARTBEAT -eq 1 ] || { echo "FAIL: register/heartbeat"; cat $LOG/server.log $LOG/agent.log; exit 1; }
echo "[2/4] agent registered + heartbeating"

# 4. 指令下发回环：一次性 gRPC 客户端调 Dispatch
NODE_ID=$(grep -oP 'node_id=\K[0-9a-f]{32}' $LOG/server.log | head -1)
[ -n "$NODE_ID" ] || { echo "FAIL: node_id not found in server log"; exit 1; }
cd $SRC/agent
go run ./cmd/dispatch "127.0.0.1:$PORT" "$NODE_ID" 2>&1 | tee $LOG/dispatch.log | grep -q "PASS" || { echo "FAIL: dispatch"; cat $LOG/dispatch.log; exit 1; }
echo "[3/4] dispatch roundtrip ok"
echo "[4/4] E2E PASS"
