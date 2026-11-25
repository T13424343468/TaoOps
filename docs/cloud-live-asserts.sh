#!/usr/bin/env bash
# TaoOps 云端 Linux 实弹断言（ci/cloud-linux 分支专用，GitHub Actions ubuntu runner）。
# 覆盖 breakpoint-m3-waf.md 待真机清单 + FR-C2 SSH 防爆破实弹 + 回流链路。
# 口径：逐条断言出数字，失败即 FAIL 退出；不虚报——每条 PASS 都有真实 curl/grep 证据。
set -u

PASS=0; FAIL=0
ok()   { echo "[PASS] $1"; PASS=$((PASS+1)); }
bad()  { echo "[FAIL] $1"; FAIL=$((FAIL+1)); }

WAFROOT=/etc/openresty/taoops-waf
EVENTS_LOG=/var/log/taoops-waf/events.log
LIVE=/tmp/taoops-live
PORT=8080
SRV_PORT=${TAOOPS_PORT:-5243}
NODE=${TAOOPS_NODE_ID:-${TAOOPS_NODE:-live-node-1}}
SEC_LOG=${TAOOPS_SEC_LOG_PATH:-/var/log/taoops-sec/events.log}

# ---------- 阶段1：装配与启动 ----------
sudo mkdir -p "$WAFROOT/lua" /var/log/taoops-waf /var/log/taoops-sec
sudo chown -R "$USER" /var/log/taoops-waf /var/log/taoops-sec
sudo cp "$LIVE"/waf/access.lua "$WAFROOT/lua/"
sudo cp "$LIVE"/waf/rules-whitelist.json "$WAFROOT/lua/rules.json"

# nginx 主配置：http 块 include waf.conf + CC 共享字典（待真机清单第2条），error_log 即事件文件
sudo tee "$LIVE/nginx.conf" >/dev/null <<EOF
worker_processes 1;
error_log $EVENTS_LOG info;
pid $LIVE/nginx.pid;
events { worker_connections 256; }
http {
    include $LIVE/waf/waf.conf;
    lua_shared_dict taoops_waf_cc 10m;
    server {
        listen $PORT;
        include $LIVE/waf/site.conf;
        location / { return 200 "ok\n"; }
    }
}
EOF

echo "== L1 OpenResty assembly & start =="
if sudo /usr/local/openresty/bin/openresty -t -c "$LIVE/nginx.conf" -p "$LIVE/" >/dev/null 2>&1; then
  ok "L1a config test"
else
  bad "L1a config test"; sudo /usr/local/openresty/bin/openresty -t -c "$LIVE/nginx.conf" -p "$LIVE/"; exit 1
fi
sudo /usr/local/openresty/bin/openresty -c "$LIVE/nginx.conf" -p "$LIVE/"
sleep 1
CODE=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/")
[ "$CODE" = "200" ] && ok "L1b server up baseline 200" || bad "L1b baseline got $CODE"

# ---------- 阶段2：白名单短路（初始规则集含 127.0.0.1 白名单 + SQLi deny）----------
echo "== L2 whitelist short-circuit =="
CODE=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/?id=1%20union%20select")
[ "$CODE" = "200" ] && ok "L2a SQLi sample allowed by 127.0.0.1 whitelist (got 200)" || bad "L2a got $CODE"

# ---------- 阶段3：热更新去白名单 → SQLi 403（热重载断言同条覆盖）----------
echo "== L3 hot reload + SQLi deny =="
START=$(date +%s)
sudo cp "$LIVE/waf/rules-noip.json" "$WAFROOT/lua/rules.json"
DETECTED=0
for i in $(seq 1 20); do   # RELOAD_CHECK_INTERVAL=5s + 余量；每次 curl 顺带触发 mtime 检查
  sleep 0.5
  CODE=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/?id=1%20union%20select")
  if [ "$CODE" = "403" ]; then DETECTED=1; break; fi
done
ELAPSED=$(( $(date +%s) - START ))
if [ "$DETECTED" = "1" ] && [ "$ELAPSED" -le 6 ]; then
  ok "L3a SQLi denied 403 after hot reload (${ELAPSED}s, no nginx reload)"
else
  bad "L3a SQLi deny after hot reload (detected=$DETECTED elapsed=${ELAPSED}s)"
fi

# ---------- 阶段4：CC 超限拦截 ----------
echo "== L4 CC rate limit =="
sudo cp "$LIVE/waf/rules-cc.json" "$WAFROOT/lua/rules.json"
sleep 6   # 等 mtime 重载窗口
CODES=""
for i in $(seq 1 8); do
  C=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/?ccburst=$i")
  CODES="$CODES $C"
done
echo "$CODES" | grep -q "403" && ok "L4 CC deny observed within burst (max 5/60s):$CODES" || bad "L4 CC deny:$CODES"

# ---------- 阶段5：事件文件 TAOOPS_WAF 行 ----------
echo "== L5 interception event log =="
if sudo grep -q "TAOOPS_WAF" "$EVENTS_LOG"; then
  ok "L5a TAOOPS_WAF lines in $EVENTS_LOG (count: $(sudo grep -c 'TAOOPS_WAF' "$EVENTS_LOG"))"
else
  bad "L5a no TAOOPS_WAF in $EVENTS_LOG"
fi

# ---------- 阶段6：SSH 防爆破实弹（真 sshd + 真 auth.log）----------
echo "== L6 SSH bruteforce (FR-C2) =="
AUTH_LOG=/var/log/auth.log
sudo chmod o+r "$AUTH_LOG" 2>/dev/null || true
for i in $(seq 1 6); do
  sshpass -p "definitely-wrong-$i" ssh -o StrictHostKeyChecking=no -o ConnectTimeout=5 \
    -o PreferredAuthentications=password -o PubkeyAuthentication=no \
    root@127.0.0.1 true >/dev/null 2>&1 || true
done
sleep 1
FAILED=$(sudo grep -c "Failed password" "$AUTH_LOG" 2>/dev/null || echo 0)
echo "  auth.log Failed password lines: $FAILED"
[ "$FAILED" -ge 5 ] && ok "L6a >=5 real failed SSH logins in auth.log" || bad "L6a only $FAILED failure lines"

SEC_OUT=$("$LIVE/bin/dispatch" "127.0.0.1:$SRV_PORT" "$NODE" sec.manage op=scan 2>&1) || true
echo "  dispatch sec.manage: $SEC_OUT"
echo "$SEC_OUT" | grep -q "RESULT exit=0" && ok "L6b secguard scan via control-plane dispatch (TLS+TOFU)" || bad "L6b secguard scan: $SEC_OUT"
grep -q "TAOOPS_SEC" "$SEC_LOG" 2>/dev/null && ok "L6c TAOOPS_SEC lockout event in $SEC_LOG" || bad "L6c no TAOOPS_SEC in $SEC_LOG"

# ---------- 阶段7：回流链路（agent tail 真实文件 → 中控 → 订阅者）----------
echo "== L7 log-stream roundtrip =="
WSPROBE_OUT=$(dotnet "$LIVE/wsprobe.dll" logs "wss://127.0.0.1:$SRV_PORT/ws/agent/$NODE/logs?path=$EVENTS_LOG&tail=0" TAOOPS_WAF 12 2>&1 || true)
curl -s -o /dev/null "http://127.0.0.1:$PORT/?id=2%20union%20select%20livefire"
sleep 1
echo "$WSPROBE_OUT" | grep -q "TAOOPS_WAF" && ok "L7 agent->server log-stream delivers TAOOPS_WAF line" || { bad "L7 roundtrip"; echo "$WSPROBE_OUT" | tail -5; }

echo "== RESULT: PASS=$PASS FAIL=$FAIL =="
[ "$FAIL" = "0" ]
