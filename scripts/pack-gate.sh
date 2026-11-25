#!/usr/bin/env bash
# TaoOps 出包硬闸（老板 2026-11-24 22:33 令「接令开工出包三道闸制度」第三道）。
# 物理拦截：任一检查不过，包组不出来，退出码非 0。
# 用法：打包流程第一步必须调用本脚本；绕过脚本出包按跳闸追责。
#
# 检查项（与 MSG-20261123T1620 出包闸四项硬清单 + 三道闸制度对齐）：
#   1. 总线当日已拉（taobus 仓当日有 fetch/pull 记录）
#   2. 五张对账表在位且无未清挂账（grep 挂账标记）
#   3. 全测绿回执：测试回执文件存在且提交号与当前 HEAD 一致
#   4. 远端可核验：HEAD 已 push（ls-remote 包含当前提交）
#   5. 以人为本必查：出包人已写看后记录（确认文件）
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUS="/root/workspace/taobus"
TODAY="$(date +%Y-%m-%d)"
RECEIPT_DIR="${REPO}/docs/pack-gate"
mkdir -p "${RECEIPT_DIR}"

fail() { echo "[出包硬闸] 拦截：$1" >&2; exit 1; }

# 1. 总线当日已拉：以 taobus 仓 .git/FETCH_HEAD 当日修改时间为准
if [[ ! -f "${BUS}/.git/FETCH_HEAD" ]] || [[ "$(date -r "${BUS}/.git/FETCH_HEAD" +%Y-%m-%d)" != "${TODAY}" ]]; then
  fail "总线今日未拉取（git -C ${BUS} pull --rebase origin 后重试）"
fi

# 2. 五张对账表：文件在位且无未清挂账标记
for t in "${REPO}"/docs/对账表-*.md; do
  [[ -e "$t" ]] || fail "对账表缺失：docs/ 下未找到 对账表-*.md（五张表口径见 MSG-20261123T1620）"
  if grep -n "挂账待\|待真机确认\|未出账" "$t" >/dev/null 2>&1; then
    fail "对账表存在未清挂账：$t（逐项清账或老板拍板落档后重试）"
  fi
done

# 3. 全测绿回执：当日回执存在且提交号与 HEAD 一致
HEAD_ID="$(git -C "${REPO}" rev-parse HEAD)"
RECEIPT="${RECEIPT_DIR}/test-green-${TODAY}.txt"
if [[ ! -f "${RECEIPT}" ]]; then
  fail "缺全测绿回执：${RECEIPT}（跑完全量测试后写入：提交号 + 各层测试结果）"
fi
grep -q "^commit=${HEAD_ID}$" "${RECEIPT}" || fail "全测绿回执提交号与 HEAD 不一致（${RECEIPT} 记的是旧提交，重跑全量测试后更新）"

# 4. 远端可核验：HEAD 已推送
if ! git -C "${REPO}" ls-remote origin refs/heads/master 2>/dev/null | grep -q "^${HEAD_ID}"; then
  fail "HEAD（${HEAD_ID::12}）未推送远端：commit 必须 push 并 ls-remote 核验后才能出包"
fi

# 5. 以人为本必查：出包人看后记录（人工确认，脚本只验存在）
HUMAN="${RECEIPT_DIR}/human-check-${TODAY}.txt"
[[ -f "${HUMAN}" ]] || fail "缺以人为本看后记录：${HUMAN}（逐项过道系总纲后写确认，格式见文件头注释）"

echo "[出包硬闸] 全部通过：${TODAY} HEAD=${HEAD_ID::12}，放行出包。"
