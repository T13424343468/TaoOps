#!/usr/bin/env bash
# TaoOps M1 端到端回环：起中控 → 起 Agent → 断言注册+心跳 → 真实模块/Shell 指令经
# CommandStream 下发到 Agent 侧分发器执行 → 结果回传
# 用法：bash e2e_loop.sh ；输出 PASS/FAIL，退出码 0/1
set -u
export PATH=/root/workspace/.local/go/bin:/root/workspace/.local/dotnet10:$PATH
export DOTNET_ROOT=/root/workspace/.local/dotnet10
export DOTNET_CLI_TELEMETRY_OPTOUT=1
SRC=/root/workspace/projects/2377946/src
PORT=5241
LOG=/tmp/taoops-e2e
mkdir -p $LOG

cleanup() { [ -n "${SERVER_PID:-}" ] && kill $SERVER_PID 2>/dev/null; [ -n "${AGENT_PID:-}" ] && kill $AGENT_PID 2>/dev/null; }
trap cleanup EXIT

# 1. 起中控（gRPC 需 HTTP/2 明文：Kestrel 开 h2c；M2 起明文须显式 TAOOPS_INSECURE=1）
cd $SRC/server
export TAOOPS_INSECURE=1
export ASPNETCORE_Kestrel__EndpointDefaults__Protocols="Http2"
# 直跑产物 dll（M3 安全班适配）：dotnet run 在沙箱静默挂起（build-server shutdown 无效），
# 直跑 dll 行为等价（脚本假定已 dotnet build -c Release）。
dotnet bin/Release/net10.0/TaoOps.Server.dll --urls "http://127.0.0.1:$PORT" > $LOG/server.log 2>&1 &
SERVER_PID=$!
for i in $(seq 1 30); do
  curl -s --http2-prior-knowledge "http://127.0.0.1:$PORT/" >/dev/null 2>&1 && break
  sleep 0.5
done
curl -s --http2-prior-knowledge "http://127.0.0.1:$PORT/" 2>/dev/null | grep -q "TaoOps Server" || { echo "FAIL: server not up"; cat $LOG/server.log; exit 1; }
echo "[1/7] server up"

# 2. 起 Agent（gRPC 明文 http；模块目录指向临时目录）
cd $SRC/agent
export TAOOPS_CONF_DIR=$LOG/conf TAOOPS_BACKUP_DIR=$LOG/backup TAOOPS_SESSION_DIR=$LOG/sessions
export TAOOPS_LOG_DIR=$LOG/logs TAOOPS_PACKAGE_DIR=$LOG/pkg TAOOPS_INSTALL_DIR=$LOG/opt TAOOPS_AUDIT_PATH=$LOG/audit.json
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
echo "[2/7] agent registered + heartbeating"

# 4. 等命令流挂接
for i in $(seq 1 20); do
  grep -q "Command stream attached" $LOG/server.log && break
  sleep 0.5
done
grep -q "Command stream attached" $LOG/server.log || { echo "FAIL: command stream not attached"; cat $LOG/server.log; exit 1; }
echo "[3/7] command stream attached"

NODE_ID=$(grep -oP 'node_id=\K[0-9a-f]{32}' $LOG/server.log | head -1)
[ -n "$NODE_ID" ] || { echo "FAIL: node_id not found in server log"; exit 1; }
cd $SRC/agent

# 5. Shell 白名单指令：echo 经 CommandStream 到 Agent 执行
go run ./cmd/dispatch "127.0.0.1:$PORT" "$NODE_ID" echo e2e-ok 2>&1 | tee $LOG/dispatch-echo.log | grep -q 'RESULT exit=0.*e2e-ok' || { echo "FAIL: echo dispatch"; cat $LOG/dispatch-echo.log $LOG/agent.log; exit 1; }
echo "[4/7] shell echo roundtrip ok"

# 6. 真实模块：sysinfo.overview
go run ./cmd/dispatch "127.0.0.1:$PORT" "$NODE_ID" sysinfo.overview 2>&1 | tee $LOG/dispatch-sysinfo.log | grep -q 'RESULT exit=0' || { echo "FAIL: sysinfo.overview"; cat $LOG/dispatch-sysinfo.log $LOG/agent.log; exit 1; }
echo "[5/7] module sysinfo.overview ok"

# 7. 真实模块：backup.manage op=list（沙箱无 crontab，cron 断言由单测覆盖）
go run ./cmd/dispatch "127.0.0.1:$PORT" "$NODE_ID" backup.manage op=list 2>&1 | tee $LOG/dispatch-backup.log | grep -q 'RESULT exit=0' || { echo "FAIL: backup.manage"; cat $LOG/dispatch-backup.log $LOG/agent.log; exit 1; }
echo "[6/7] module backup.manage ok"

# 8. Shell 白名单：df
go run ./cmd/dispatch "127.0.0.1:$PORT" "$NODE_ID" df 2>&1 | tee $LOG/dispatch-df.log | grep -q 'RESULT exit=0' || { echo "FAIL: df"; cat $LOG/dispatch-df.log $LOG/agent.log; exit 1; }
echo "[7/7] shell df ok"
echo "E2E PASS"
