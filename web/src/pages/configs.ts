import type { ModulePageConfig } from "./ModulePage";

// Per-page configs for command-backed modules. Column/rows mirror the
// prototype pages (TaoOps 全套原型.html sections pg-*).
export const SITE_PAGE: ModulePageConfig = {
  title: "网站管理",
  description: "pg-site",
  columns: [{ key: "domain", title: "域名" }, { key: "root", title: "根目录" }, { key: "php", title: "PHP" }, { key: "status", title: "状态" }],
  rows: [
    { domain: "example.com", root: "/www/example.com", php: "8.2", status: "运行中" },
    { domain: "api.example.com", root: "/www/api", php: "无（反代）", status: "运行中" },
  ],
  commands: [
    { label: "启动", command: "site.manage", args: ["start"] },
    { label: "停止", command: "site.manage", args: ["stop"] },
    { label: "重载配置", command: "site.manage", args: ["reload"] },
  ],
};

export const DB_PAGE: ModulePageConfig = {
  title: "数据库",
  description: "pg-db",
  columns: [{ key: "name", title: "库名" }, { key: "engine", title: "类型" }, { key: "size", title: "大小" }, { key: "backupAt", title: "最近备份" }],
  rows: [
    { name: "app_prod", engine: "MySQL 8.0", size: "1.2 GB", backupAt: "2026-11-23 03:00" },
    { name: "app_staging", engine: "MySQL 8.0", size: "310 MB", backupAt: "2026-11-23 03:00" },
  ],
  commands: [
    { label: "立即备份", command: "db.manage", args: ["backup"] },
    { label: "优化表", command: "db.manage", args: ["optimize"] },
  ],
};

export const FILES_PAGE: ModulePageConfig = {
  title: "文件管理",
  description: "pg-files",
  columns: [{ key: "path", title: "路径" }, { key: "size", title: "大小" }, { key: "mtime", title: "修改时间" }, { key: "owner", title: "属主" }],
  rows: [
    { path: "/www/example.com/index.html", size: "4.2 KB", mtime: "2026-11-22 18:20", owner: "www" },
    { path: "/www/example.com/uploads", size: "—", mtime: "2026-11-22 09:14", owner: "www" },
  ],
  commands: [
    { label: "打包下载", command: "files.manage", args: ["pack"] },
    { label: "删除", command: "files.manage", args: ["delete"] },
  ],
};

export const CRON_PAGE: ModulePageConfig = {
  title: "计划任务",
  description: "pg-cron",
  columns: [{ key: "name", title: "任务名" }, { key: "schedule", title: "周期" }, { key: "command", title: "命令" }, { key: "lastRun", title: "上次执行" }],
  rows: [
    { name: "日志轮转", schedule: "每天 02:00", command: "logrotate /etc/logrotate.conf", lastRun: "2026-11-23 02:00 成功" },
    { name: "数据库备份", schedule: "每天 03:00", command: "backup.sh --full", lastRun: "2026-11-23 03:00 成功" },
  ],
  commands: [
    { label: "立即执行", command: "cron.manage", args: ["run"] },
    { label: "停用", command: "cron.manage", args: ["disable"] },
  ],
};

export const ENV_PAGE: ModulePageConfig = {
  title: "环境变量",
  description: "pg-env",
  columns: [{ key: "key", title: "变量" }, { key: "value", title: "值" }, { key: "scope", title: "作用域" }],
  rows: [
    { key: "APP_ENV", value: "production", scope: "全局" },
    { key: "DB_HOST", value: "127.0.0.1", scope: "app.service" },
  ],
  commands: [{ label: "重载环境", command: "env.manage", args: ["reload"] }],
};

export const FTP_PAGE: ModulePageConfig = {
  title: "FTP 管理",
  description: "pg-ftp",
  columns: [{ key: "user", title: "账号" }, { key: "root", title: "根目录" }, { key: "status", title: "状态" }],
  rows: [
    { user: "deploy", root: "/www/example.com", status: "启用" },
  ],
  commands: [
    { label: "停用", command: "ftp.manage", args: ["disable"] },
    { label: "重置密码", command: "ftp.manage", args: ["chpasswd"] },
  ],
};

export const MONITOR_PAGE: ModulePageConfig = {
  title: "监控告警",
  description: "pg-monitor",
  columns: [{ key: "name", title: "告警规则" }, { key: "cond", title: "条件" }, { key: "channel", title: "通知" }, { key: "state", title: "状态" }],
  rows: [
    { name: "CPU 持续高载", cond: "cpu > 90% 持续 5 分钟", channel: "Webhook", state: "启用" },
    { name: "磁盘将满", cond: "disk > 90%", channel: "Webhook", state: "启用" },
  ],
  commands: [{ label: "静默 1h", command: "monitor.silence", args: ["3600"] }],
};

export const AI_PAGE: ModulePageConfig = {
  title: "AI 原生运维",
  description: "pg-ai（本地模型默认不装、可选安装，内存预算 ≤2GB）",
  columns: [{ key: "capability", title: "能力" }, { key: "model", title: "模型" }, { key: "state", title: "状态" }],
  rows: [
    { capability: "日志异常解读", model: "本地小模型（未安装）", state: "未启用" },
    { capability: "告警处置建议", model: "规则引擎（内置）", state: "启用" },
  ],
  commands: [{ label: "生成建议", command: "ai.advise" }],
};

export const UPGRADE_PAGE: ModulePageConfig = {
  title: "升级与规则包",
  description: "pg-upgrade（离线导入）",
  columns: [{ key: "pkg", title: "包" }, { key: "version", title: "版本" }, { key: "channel", title: "来源" }, { key: "state", title: "状态" }],
  rows: [
    { pkg: "taoops-server", version: "0.1.0 → 0.2.0", channel: "离线导入", state: "待执行" },
    { pkg: "waf-rules", version: "2026.11.2", channel: "离线导入", state: "已更新" },
  ],
  commands: [{ label: "执行升级", command: "upgrade.run" }, { label: "回滚", command: "upgrade.rollback" }],
};

export const LB_PAGE: ModulePageConfig = {
  title: "负载均衡",
  description: "pg-lb",
  columns: [{ key: "name", title: "上游组" }, { key: "backend", title: "后端" }, { key: "weight", title: "权重" }, { key: "health", title: "健康" }],
  rows: [
    { name: "web-pool", backend: "10.0.0.11:8080", weight: 50, health: "健康" },
    { name: "web-pool", backend: "10.0.0.12:8080", weight: 50, health: "健康" },
  ],
  commands: [{ label: "摘除节点", command: "lb.drain" }],
};

export const SSL_PAGE: ModulePageConfig = {
  title: "SSL 证书",
  description: "pg-ssl",
  columns: [{ key: "domain", title: "域名" }, { key: "issuer", title: "签发方" }, { key: "expires", title: "到期" }, { key: "autoRenew", title: "自动续期" }],
  rows: [
    { domain: "example.com", issuer: "Let's Encrypt", expires: "2027-02-10", autoRenew: "开" },
  ],
  commands: [{ label: "立即续期", command: "ssl.renew" }],
};

export const PUSH_PAGE: ModulePageConfig = {
  title: "告警推送",
  description: "pg-push",
  columns: [{ key: "channel", title: "渠道" }, { key: "target", title: "目标" }, { key: "state", title: "状态" }],
  rows: [
    { channel: "Webhook", target: "https://hooks.example.com/taoops", state: "启用" },
  ],
  commands: [{ label: "测试推送", command: "push.test" }],
};

export const NODES_PAGE: ModulePageConfig = {
  title: "多机中控",
  description: "pg-nodes",
  columns: [{ key: "nodeId", title: "节点" }, { key: "hostname", title: "主机名" }, { key: "os", title: "系统" }, { key: "tags", title: "标签" }, { key: "status", title: "状态" }],
  rows: [
    { nodeId: "node-01", hostname: "web-01", os: "ubuntu-22.04", tags: "web", status: "在线" },
    { nodeId: "node-02", hostname: "db-01", os: "ubuntu-22.04", tags: "db", status: "在线" },
  ],
  commands: [
    { label: "批量下发（跨机）", command: "dispatch.broadcast" },
    { label: "安装 Agent", command: "dispatch.install-agent" },
  ],
};

export const WEBSTATS_PAGE: ModulePageConfig = {
  title: "网站统计",
  description: "pg-webstats",
  columns: [
    { key: "date", title: "日期" }, { key: "pv", title: "PV" }, { key: "uv", title: "UV（估算）" },
    { key: "status", title: "状态码分布" }, { key: "bytes", title: "流量" }, { key: "top", title: "Top 来源/URL" },
  ],
  rows: [
    { date: "2026-11-24", pv: "12,480", uv: "1,032", status: "200: 11,900 / 404: 412", bytes: "3.2 GB", top: "1.2.3.4 (320) · /api/list (890)" },
    { date: "2026-11-23", pv: "11,204", uv: "986", status: "200: 10,780 / 502: 96", bytes: "2.9 GB", top: "5.6.7.8 (271) · /login (744)" },
  ],
  commands: [
    { label: "解析日志", command: "webstats.manage", args: ["op=parse"] },
    { label: "查询汇总", command: "webstats.manage", args: ["op=summary"] },
    { label: "导出报表", command: "webstats.manage", args: ["op=export"] },
    { label: "清空统计", command: "webstats.manage", args: ["op=clear"] },
  ],
};

export const DOCKER_PAGE: ModulePageConfig = {
  title: "Docker 管理",
  description: "pg-docker",
  columns: [
    { key: "name", title: "容器" }, { key: "image", title: "镜像" }, { key: "state", title: "状态" },
    { key: "ports", title: "端口" }, { key: "status", title: "运行详情" },
  ],
  rows: [
    { name: "web", image: "nginx:1.25", state: "running", ports: "80/tcp", status: "Up 2 hours" },
    { name: "db", image: "mysql:8.0", state: "exited", ports: "3306/tcp", status: "Exited (0) 3 hours ago" },
  ],
  commands: [
    { label: "启动", command: "docker.manage", args: ["op=start"] },
    { label: "停止", command: "docker.manage", args: ["op=stop"] },
    { label: "重启", command: "docker.manage", args: ["op=restart"] },
    { label: "日志", command: "docker.manage", args: ["op=logs"] },
    { label: "进终端", command: "docker.manage", args: ["op=terminal"] },
    { label: "拉取镜像", command: "docker.manage", args: ["op=image_pull"] },
    { label: "删除镜像", command: "docker.manage", args: ["op=image_remove"] },
  ],
};
