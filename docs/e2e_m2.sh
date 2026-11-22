#!/usr/bin/env bash
# TaoOps M2 端到端（安全模式）：TLS+TOFU 加密链路 + WebSocket 终端/日志桥 + 跨节点下发 + FR-A8
# 用法：bash e2e_m2.sh ；输出 PASS/FAIL，退出码 0/1
# 口径（契约 §1）：中控默认 TLS（自签证书落数据目录）；Agent 首连 TOFU 固化指纹，
# 二连校验；指纹不匹配立即拒绝。TAOOPS_INSECURE=1 仅限本地回环，本脚本全程不使用。
set -u
export PATH=/root/workspace/.local/go/bin:/root/workspace/.local/dotnet10:$PATH
export DOTNET_ROOT=/root/workspace/.local/dotnet10
export DOTNET_CLI_TELEMETRY_OPTOUT=1
SRC=/root/workspace/projects/2377946/src
PORT=5242
LOG=/tmp/taoops-e2e-m2
rm -rf $LOG && mkdir -p $LOG

# 预编译：直接运行二进制，kill 才能真正终止（go run 包装进程会留孤儿）
(cd $SRC/agent && go build -o $LOG/bin/agent ./cmd/agent && go build -o $LOG/bin/dispatch ./cmd/dispatch) \
  || { echo "FAIL: agent build"; exit 1; }

SERVER_PID=""; AGENT_PID=""; AGENT2_PID=""
cleanup() {
  [ -n "${AGENT_PID:-}" ] && kill $AGENT_PID 2>/dev/null
  [ -n "${AGENT2_PID:-}" ] && kill $AGENT2_PID 2>/dev/null
  [ -n "${SERVER_PID:-}" ] && kill $SERVER_PID 2>/dev/null
}
trap cleanup EXIT

start_server() {
  cd $SRC/server
  # 直跑产物 dll（M3 安全班适配）：dotnet run 在沙箱静默挂起，直跑 dll 行为等价。
  env -u ASPNETCORE_URLS -u ASPNETCORE_Kestrel__EndpointDefaults__Protocols \
    TAOOPS_DATA_DIR=$LOG/srvdata TAOOPS_PORT=$PORT \
    dotnet bin/Release/net10.0/TaoOps.Server.dll > $LOG/server.log 2>&1 &
  SERVER_PID=$!
  for i in $(seq 1 40); do
    curl -sk "https://127.0.0.1:$PORT/" >/dev/null 2>&1 && return 0
    sleep 0.5
  done
  return 1
}

start_agent() { # $1=agent_id $2=logfile
  TAOOPS_SERVER=127.0.0.1:$PORT TAOOPS_AGENT_ID=$1 TAOOPS_KNOWN_HOSTS=$LOG/known_hosts \
    TAOOPS_CONF_DIR=$LOG/conf TAOOPS_BACKUP_DIR=$LOG/backup TAOOPS_SESSION_DIR=$LOG/sessions \
    TAOOPS_LOG_DIR=$LOG/logs TAOOPS_PACKAGE_DIR=$LOG/pkg TAOOPS_INSTALL_DIR=$LOG/opt \
    TAOOPS_AUDIT_PATH=$LOG/audit.json \
    $LOG/bin/agent > $2 2>&1 &
}

wait_agent_registered() { # $1=logfile
  for i in $(seq 1 40); do
    grep -q "registered as node" $1 && return 0
    sleep 0.5
  done
  return 1
}

WSPROBE="dotnet $SRC/tools/wsprobe/bin/Release/net10.0/wsprobe.dll"

# 1. 安全模式起中控，断言自签证书落盘
start_server || { echo "FAIL: secure server not up"; cat $LOG/server.log; exit 1; }
curl -sk "https://127.0.0.1:$PORT/" | grep -q "TaoOps Server M2" || { echo "FAIL: root text"; cat $LOG/server.log; exit 1; }
[ -f $LOG/srvdata/server.pfx ] || { echo "FAIL: server.pfx not created"; exit 1; }
echo "[1/9] secure server up, pfx created"

# 2. Agent #1 首连：TLS+TOFU 固化指纹
start_agent e2e-m2-agent-001 $LOG/agent1.log
AGENT_PID=$!
wait_agent_registered $LOG/agent1.log || { echo "FAIL: agent1 first connect"; cat $LOG/agent1.log; exit 1; }
[ -s $LOG/known_hosts ] || { echo "FAIL: known_hosts not pinned"; cat $LOG/agent1.log; exit 1; }
grep -qE '^127\.0\.0\.1 [0-9a-f]{64}$' $LOG/known_hosts || { echo "FAIL: bad pin format"; cat $LOG/known_hosts; exit 1; }
echo "[2/9] agent1 TOFU first-connect pinned"

# 3. Agent #1 重启二连：指纹校验通过
kill $AGENT_PID; wait $AGENT_PID 2>/dev/null; AGENT_PID=""
sleep 2  # 等中控摘除旧流会话，避免重连撞 AlreadyExists
start_agent e2e-m2-agent-001 $LOG/agent1b.log
AGENT_PID=$!
wait_agent_registered $LOG/agent1b.log || { echo "FAIL: agent1 second connect (pin verify)"; cat $LOG/agent1b.log; exit 1; }
echo "[3/9] agent1 reconnect pin-verified"

NODE1=$(grep -oP 'node_id=\K[0-9a-f]{32}' $LOG/server.log | head -1)
[ -n "$NODE1" ] || { echo "FAIL: node1 id not found"; exit 1; }

# 4. WebSocket 终端：发 echo 断言回显（bash -i 会话）
$WSPROBE term "wss://127.0.0.1:$PORT/ws/agent/$NODE1/terminal" - "echo ws-ok" \
  > $LOG/ws-term.log 2>&1 || { echo "FAIL: ws terminal"; cat $LOG/ws-term.log $LOG/server.log; exit 1; }
grep -q "PASS term echo" $LOG/ws-term.log || { echo "FAIL: ws terminal assert"; cat $LOG/ws-term.log; exit 1; }
echo "[4/9] ws terminal echo ok"

# 5. WebSocket 日志：订阅文件，追加内容断言到达
touch $LOG/m2log.txt
$WSPROBE logs "wss://127.0.0.1:$PORT/ws/agent/$NODE1/logs?path=$LOG/m2log.txt&tail=5" m2-log-marker-42 \
  > $LOG/ws-logs.log 2>&1 &
WSPROBE_PID=$!
sleep 2
echo "$(date +%T) m2-log-marker-42 hello" >> $LOG/m2log.txt
for i in $(seq 1 20); do
  grep -q "PASS logs" $LOG/ws-logs.log && break
  kill -0 $WSPROBE_PID 2>/dev/null || break
  sleep 0.5
done
grep -q "PASS logs" $LOG/ws-logs.log || { echo "FAIL: ws logs"; cat $LOG/ws-logs.log $LOG/agent1b.log; exit 1; }
echo "[5/9] ws log stream ok"

# 6. 跨节点：Agent #2 入网，双节点各自下发 echo
start_agent e2e-m2-agent-002 $LOG/agent2.log
AGENT2_PID=$!
wait_agent_registered $LOG/agent2.log || { echo "FAIL: agent2 register"; cat $LOG/agent2.log; exit 1; }
NODE2=$(grep "agent_id=e2e-m2-agent-002" $LOG/server.log | grep -oP 'node_id=\K[0-9a-f]{32}' | head -1)
[ -n "$NODE2" ] || { echo "FAIL: node2 id not found"; exit 1; }
TAOOPS_KNOWN_HOSTS=$LOG/known_hosts.dispatch $LOG/bin/dispatch "127.0.0.1:$PORT" "$NODE1" echo from-node1 2>&1 | tee $LOG/d1.log | grep -q 'RESULT exit=0.*from-node1' || { echo "FAIL: dispatch node1"; cat $LOG/d1.log $LOG/agent1b.log; exit 1; }
TAOOPS_KNOWN_HOSTS=$LOG/known_hosts.dispatch $LOG/bin/dispatch "127.0.0.1:$PORT" "$NODE2" echo from-node2 2>&1 | tee $LOG/d2.log | grep -q 'RESULT exit=0.*from-node2' || { echo "FAIL: dispatch node2"; cat $LOG/d2.log $LOG/agent2.log; exit 1; }
echo "[6/9] cross-node dispatch ok (node1+node2)"

# 7. FR-A8：ftp.manage 真实下发 adduser/list
TAOOPS_KNOWN_HOSTS=$LOG/known_hosts.dispatch $LOG/bin/dispatch "127.0.0.1:$PORT" "$NODE1" ftp.manage op=adduser user=u1 password=pw1 root=$LOG/ftproot 2>&1 | grep -q 'RESULT exit=0' || { echo "FAIL: ftp adduser"; exit 1; }
TAOOPS_KNOWN_HOSTS=$LOG/known_hosts.dispatch $LOG/bin/dispatch "127.0.0.1:$PORT" "$NODE1" ftp.manage op=list 2>&1 | grep -q 'RESULT exit=0' || { echo "FAIL: ftp list"; exit 1; }
grep -q '"u1"' $LOG/conf/ftp.json || { echo "FAIL: ftp user not persisted"; cat $LOG/conf/ftp.json; exit 1; }
echo "[7/9] ftp.manage adduser/list ok"

# 8. 白名单外模块指令仍被拒（安全回归）
TAOOPS_KNOWN_HOSTS=$LOG/known_hosts.dispatch $LOG/bin/dispatch "127.0.0.1:$PORT" "$NODE1" kernel.tamper 2>&1 | grep -q 'FAIL: dispatch' || { echo "FAIL: blacklist not enforced"; exit 1; }
echo "[8/9] command whitelist still enforced"

# 9. 指纹不匹配：换证书重启中控，Agent 必须拒绝连接
kill $AGENT_PID; wait $AGENT_PID 2>/dev/null; AGENT_PID=""
kill $AGENT2_PID; wait $AGENT2_PID 2>/dev/null; AGENT2_PID=""
kill $SERVER_PID; wait $SERVER_PID 2>/dev/null; SERVER_PID=""
mv $LOG/srvdata $LOG/srvdata.old
start_server || { echo "FAIL: server restart"; cat $LOG/server.log; exit 1; }
start_agent e2e-m2-agent-001 $LOG/agent-mismatch.log
AGENT_PID=$!
for i in $(seq 1 30); do
  grep -q "fingerprint mismatch" $LOG/agent-mismatch.log && break
  sleep 0.5
done
grep -q "fingerprint mismatch" $LOG/agent-mismatch.log || { echo "FAIL: mismatch not rejected"; cat $LOG/agent-mismatch.log; exit 1; }
grep -q "registered as node" $LOG/agent-mismatch.log && { echo "FAIL: agent connected despite mismatch"; exit 1; }
echo "[9/9] fingerprint mismatch rejected"
echo "E2E M2 PASS"
