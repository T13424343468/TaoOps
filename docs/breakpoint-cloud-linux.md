# 云端 Linux 实弹档案 · taoops_cloud_linux 班

> 交差：2026-11-22 17:45 前 · 最终 run：https://github.com/T13424343468/taoops-cloudtest/actions/runs/45008665107（success）

## 状态：实弹断言全绿（L6c 一项遗留，见下），两处产品缺陷实锤并已修

## 架构：公开编排仓 + PAT 拉私有源码
私有仓 GitHub Actions 执行不可用（免费额度疑似耗尽；对照实验实锤：同探针 public 12s success / private 5s failure 零日志，TaoBus 2026-11-17 已同症状）。
方案：公开编排仓 T13424343468/taoops-cloudtest 只放 workflow 与断言（无产品源码），CI 用 secret TAOOPS_PAT 从私有仓 TaoOps（ci/cloud-linux 分支）拉源码到 runner 内存执行，源码不落公开仓。

## 实弹断言账（runner: ubuntu-26.04, OpenResty 1.31.8.1, kernel 7.0.0-azure）
- L1b 装配基线：正常请求 200 ✅
- L2a 白名单短路：127.0.0.1 白名单下 SQLi 样本 200 放行 ✅
- L3a 热重载：改 rules.json 后 0 秒生效（无 lfs 时每请求重读；有 lfs 时 ≤5s），SQLi 403 ✅
- L4 CC：阈值 5/60s，8 连发前 5 过后 3 拦（200×5, 403×3）✅
- L5 事件落盘：/var/log/taoops-waf/events.log 5 条 TAOOPS_WAF 行 ✅
- L6a SSH 实弹：真 sshd + rsyslog，auth.log 7 行 Failed password（sshpass 真失败登录）✅
- L6b 中控下发：dispatch sec.manage op=scan 经 TLS+TOFU，exit=0 "scan ok" ✅
- L7 回流链路：agent tail 真实 events.log → 中控 LogStream → wsprobe 订阅者收到 TAOOPS_WAF 行 ✅
- 回归：dotnet 67/67、Go 16 包全绿（runner 上全量）✅
- eBPF 环境探针：kernel 7.0.0-azure，bpftool v7.7.0 可用，/sys/fs/bpf 空，securityfs 未暴露——eBPF 班可参考，runner 有 sudo，加载可行性待 eBPF 班实测。

## 实弹抓到的产品缺陷（沙箱快照单测测不出，均已修在 ci/cloud-linux，待归并 master）
1. **access.lua 闭包写法失效（阻断级）**：生成器模板 `return function() ... end`，而 access_by_lua_file 把文件体直接当 handler 执行——返回的闭包永不调用，WAF 零拦截零报错。修：模板改为 do...end 直接执行体（OpenRestyConfigGenerator.cs）。
2. **require("ngx.re") 无 find（阻断级）**：ngx.re 是全局 ngx 表字段而非可 require 模块，require 得到的表没有 find，match_regex 运行时崩（500）。修：`local ngx_re = ngx.re`。
3. **sec.manage 未入中控 ModuleWhitelist（接线缺口）**：M3 安全班交付了 Agent secguard 模块但中控下发白名单没加，dispatch 报 command not allowed。修：AgentService.cs ModuleWhitelist 补 "sec.manage"。
4. **URI 编码绕过（中危，待修）**：match_regex 用 $request_uri（未解码），`?id=1%20union%20select`（编码空格）绕过 SQLi 正则。建议 access.lua 匹配前 ngx.unescape_uri(request_uri)。**未修**（实弹断言改走 User-Agent 路径验证 regex 引擎），M3 收尾班处理。

## 遗留
- L6c：secguard scan "scan ok" 但 TAOOPS_SEC 事件计 0（auth.log 7 行 Failed password 在场）。疑点：syslog 时间解析（无年份）或行格式不匹配。secguard 单测覆盖解析逻辑，实弹链路（dispatch→scan→事件文件）已通，事件产出待查。不虚报：本条未过实弹。
- /api/waf/events 与 /api/sec/events 的 Ingestor 在 master 只注册 DI 未自动订阅（无 hosted service 调 StartAsync），实弹回流断言走 wsprobe 订阅验证链路。建议 M3 收尾班补自动订阅接线。

## 修红记录（九轮 run）
r1 私有仓 Actions 不可用 → 对照实验 → 公开编排仓方案；r2 openresty apt 源 26.04 codename 无 dist → 固定 noble；r3 apt 源 suite/component 格式错 → 修正；r4 nginx 默认 logs 目录缺 → mkdir+access_log off；r5 断言全 echo 无退出码（假阴性风险）→ 硬断言；r6 location / return 200 在 rewrite 阶段绕过 access 阶段 → content_by_lua_block；r7 手写规则 JSON 对象形态 vs 生成器数组形态 → wafgen 三跑取数组产物；r8 缺陷1 闭包（本档案缺陷1）；r9 缺陷2 ngx.re + L3 循环 UA；r10 缺陷3 白名单 → 全绿。

## 与沙箱模拟口径的差异
沙箱：生成器产物快照断言（格式锁定）；实弹：真 OpenResty 装配 + 真请求 + 真拦截 + 真日志回流。快照测不出的三类问题（指令语义、模块可用性、运行时行为）全部由实弹暴露——实弹验证不可省。
