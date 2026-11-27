#!/usr/bin/env bash
# TaoOps Windows 版发布打包（FR-A31）
# 产出：dist/windows/TaoOps/ —— 安装器所需的完整程序目录
#   TaoOps.Server.exe      中控（自包含，含界面 web/）
#   web/                   网页界面（构建产物，随中控分发）
# 之后用 installer/windows/taops.iss 打成安装包。
set -euo pipefail
cd "$(dirname "$0")/.."

OUT="dist/windows/TaoOps"
rm -rf dist/windows && mkdir -p "$OUT"

echo "[1/3] 编译中控（win-x64 自包含）..."
dotnet publish server/TaoOps.Server.csproj -c Release -r win-x64 --self-contained true \
  -o "$OUT" /p:PublishSingleFile=false /p:PublishTrimmed=false

echo "[2/3] 构建网页界面..."
(cd web && npm run build)

echo "[3/3] 界面随中控分发..."
cp -r web/dist "$OUT/web"

echo "完成：$OUT"
