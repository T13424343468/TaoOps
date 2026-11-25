// Page smoke tests: one per prototype page (19 pages full coverage, v3.16).
// Real-API pages are rendered with fetch mocked; mock-backed pages render as-is.
import { describe, expect, it, vi, beforeAll } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { ConfigProvider } from "antd";
import zhCN from "antd/locale/zh_CN";

import LoginPage from "./pages/Login";
import DashPage from "./pages/Dash";
import FilesPage from "./pages/Files";
import MetricsPage from "./pages/Metrics";
import AppDeployPage from "./pages/AppDeploy";
import K8sPage from "./pages/K8s";
import DockerPage from "./pages/Docker";
import WebstatsPage from "./pages/Webstats";
import CronPage from "./pages/Cron";
import SitePage from "./pages/Site";
import DbPage from "./pages/Db";
import WafPage from "./pages/Waf";
import AuditPage from "./pages/Audit";
import TermPage from "./pages/Term";
import BackupPage from "./pages/Backup";
import MigratePage from "./pages/Migrate";
import LbPage from "./pages/Lb";
import TotpPage from "./pages/Totp";
import PushPage from "./pages/Push";
import LogCleanPage from "./pages/LogClean";
import DbReplicaPage from "./pages/DbReplica";
import SslPage from "./pages/Ssl";
import ModulePage from "./pages/ModulePage";
import {
  AI_PAGE, CRON_PAGE, DB_PAGE, ENV_PAGE, FTP_PAGE,
  NODES_PAGE, SITE_PAGE, UPGRADE_PAGE,
} from "./pages/configs";

beforeAll(() => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const u = String(url);
    const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });
    if (u.includes("/api/waf/rules")) return json([]);
    if (u.includes("/api/waf/events")) return json([]);
    if (u.includes("/api/audit/verify")) return json({ valid: true, broken_at_seq: null });
    if (u.includes("/api/audit")) return json([]);
    if (u.includes("/api/auth/users") || u.includes("/api/auth/lockouts")) return json([]);
    if (u.includes("/api/auth/me")) return json({ user: "admin", role: "Admin" });
    if (u.includes("/cmd")) return json({ exit_code: 0, output: JSON.stringify({ entries: [{ name: "nginx.conf", is_dir: false, size: 12, mode: "-rw-r--r--" }] }) });
    if (u.includes("/files/edit")) return json({ exit_code: 0, output: "" });
    if (u.includes("/backup/policy")) return json({ exit_code: 0, output: "[]" });
    if (u.includes("/db/grants")) return json({ exit_code: 0, output: "[]" });
    if (u.includes("/metrics")) return json([{ ts: Date.now(), cpu_used_pct: 12.5, mem_total_kb: 16000000, mem_used_kb: 8000000, disk_read_bps: 0, disk_write_bps: 0, net_rx_total: 0, net_tx_total: 0 }]);
    if (u.includes("/apps")) return json({ exit_code: 0, output: JSON.stringify({ templates: [{ id: "nginx", name: "Nginx 网页服务器", arch: "amd64+arm64", kind: "single", services: [{ name: "nginx", image: "nginx:stable" }] }], apps: [{ app: "my-site", template_id: "nginx", images: ["nginx:stable"], deployed_at: "2026-11-24T15:00:00Z", health: "healthy" }] }) });
    if (u.includes("/api/k8s/clusters")) return json([{ name: "prod", server: "https://10.0.0.1:6443", version: "v1.30.0", added_ms: Date.now() }]);
    if (u.includes("/api/k8s/")) return json([]);
    if (u.includes("/webstats")) return json({ exit_code: 0, output: JSON.stringify([
      { date: "2026-11-25", site: "my-site", pv: 1234, uv: 890, status: { "200": 1200, "404": 34 }, bytes: 987654321, degraded: false, top_ips: [{ key: "1.2.3.4", count: 56 }], top_urls: [{ key: "/index.html", count: 78 }] },
    ]) });
    if (u.includes("/docker")) return json({ exit_code: 0, output: JSON.stringify({
      containers: [{ id: "abc123", names: ["web"], image: "nginx:1.25", state: "running", status: "Up 2 hours", ports: "80/tcp" }],
      images: [{ repo: "nginx", tag: "1.25", id: "a1b2c3", size: "187MB", created: "2 weeks ago" }],
      mirrors: ["https://docker.m.daocloud.io"],
      registries: ["registry.example.com:5000"],
    }) });
    if (u.includes("/cron")) return json({ exit_code: 0, output: JSON.stringify({ jobs: [{ name: "nightly-backup", schedule: "0 2 * * *", command: "/usr/local/bin/backup.sh --full" }] }) });
    if (u.includes("/site")) return json({ exit_code: 0, output: JSON.stringify({ sites: [{ name: "my-site", config: "server { listen 80; }", server_name: "example.com", proxy_pass: "http://127.0.0.1:3000" }] }) });
    if (u.includes("/db/ops")) return json({ exit_code: 0, output: JSON.stringify(["mysql-app_prod-20261125030000.sql"]) });
    if (u.includes("/api/nodes")) return json([
      { nodeId: "node-abcdef1234567890", hostname: "web-01", lastSeenMs: Date.now(), registeredAtMs: Date.now(), commandConnected: true },
    ]);
    if (u.includes("/api/auth/totp/status")) return json({ enabled: false });
    if (u.includes("/api/sec/events")) return json([]);
    return json([]);
  }));
});

const renderAt = (el: React.ReactNode, path = "/") =>
  render(
    <ConfigProvider locale={zhCN}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="*" element={el} />
        </Routes>
      </MemoryRouter>
    </ConfigProvider>,
  );

describe("23 页全覆盖冒烟", () => {
  it("登录页：登录与初始化两个页签", () => {
    renderAt(<LoginPage onLogin={() => {}} />);
    expect(screen.getByText("TaoOps 控制台")).toBeTruthy();
    expect(screen.getByText("首次初始化")).toBeTruthy();
  });

  it("总览页：节点健康表", async () => {
    renderAt(<DashPage />);
    expect(screen.getByText("节点健康")).toBeTruthy();
    await waitFor(() => expect(screen.getByText("web-01")).toBeTruthy());
  });

  it("WAF 页：规则表与操作", async () => {
    renderAt(<WafPage />);
    await waitFor(() => expect(screen.getByText("新增规则")).toBeTruthy());
    expect(screen.getByText("预览生成配置")).toBeTruthy();
  });

  it("审计页：四张页签", async () => {
    renderAt(<AuditPage />);
    await waitFor(() => expect(screen.getByText("操作审计")).toBeTruthy());
    expect(screen.getByText("用户与角色")).toBeTruthy();
    expect(screen.getByText("登录锁定")).toBeTruthy();
    expect(screen.getByText("防爆破事件")).toBeTruthy();
  });

  it("终端页：节点选择与连接", () => {
    renderAt(<TermPage />);
    expect(screen.getByText("终端")).toBeTruthy();
    expect(screen.getByText("连接节点")).toBeTruthy();
  });

  it("备份页：备份列表与跨节点发送", async () => {
    renderAt(<BackupPage />);
    await waitFor(() => expect(screen.getByText("备份档案")).toBeTruthy());
    expect(screen.getByText("发送到其他节点")).toBeTruthy();
    expect(screen.getByText("中转流量统计")).toBeTruthy();
    expect(screen.getByText("定时备份策略")).toBeTruthy();
    expect(screen.getByText("添加策略")).toBeTruthy();
  });

  it("迁移页：四步迁移流程", async () => {
    renderAt(<MigratePage />);
    await waitFor(() => expect(screen.getByText("一键迁移")).toBeTruthy());
    expect(screen.getByText("1. 预检（源节点）")).toBeTruthy();
    expect(screen.getByText("2. 打包（源节点）")).toBeTruthy();
    expect(screen.getByText("4. 目标端还原（confirm）")).toBeTruthy();
  });

  it("负载均衡页：上游组列表与添加后端（真连 lb 端点）", async () => {
    renderAt(<LbPage />);
    await waitFor(() => expect(screen.getByText("负载均衡")).toBeTruthy());
    expect(screen.getByText("上游组与后端")).toBeTruthy();
    expect(screen.getByText("添加后端")).toBeTruthy();
    expect(screen.getByText("健康检查")).toBeTruthy();
  });

  it("两步验证页：状态卡与绑定入口（真连 totp 端点）", async () => {
    renderAt(<TotpPage />);
    await waitFor(() => expect(screen.getByText("两步验证（TOTP）")).toBeTruthy());
    expect(screen.getByText("当前状态")).toBeTruthy();
    await waitFor(() => expect(screen.getByText("未启用")).toBeTruthy());
    expect(screen.getByText("1. 生成绑定密钥（需密码复验）")).toBeTruthy();
  });

  const modulePages: [string, React.ReactNode, string][] = [
    ["多机中控", <ModulePage config={NODES_PAGE} />, "批量下发（跨机）"],
    ["网站", <ModulePage config={SITE_PAGE} />, "重载配置"],
    ["数据库", <ModulePage config={DB_PAGE} />, "立即备份"],
    ["FTP", <ModulePage config={FTP_PAGE} />, "重置密码"],
    ["计划任务", <ModulePage config={CRON_PAGE} />, "立即执行"],
    ["环境变量", <ModulePage config={ENV_PAGE} />, "重载环境"],
    ["AI 运维", <ModulePage config={AI_PAGE} />, "生成建议"],
    ["升级", <ModulePage config={UPGRADE_PAGE} />, "执行升级"],
  ];
  for (const [name, el, probe] of modulePages) {
    it(`${name}页：mock 提示与操作按钮`, () => {
      renderAt(el);
      expect(screen.getByText(probe)).toBeTruthy();
    });
  }

  it("监控页：实时曲线与进程 Top10（真连 metrics 端点）", async () => {
    renderAt(<MetricsPage />);
    await waitFor(() => expect(screen.getByText("资源监控")).toBeTruthy());
    await waitFor(() => expect(screen.getByText(/CPU 12\.5%/)).toBeTruthy());
    expect(screen.getByText("近 1 小时")).toBeTruthy();
    expect(screen.getByText("近 24 小时")).toBeTruthy();
    expect(screen.getByText("进程 Top10（按 CPU）")).toBeTruthy();
  });

  it("应用部署页：模板库与已部署应用（真连 apps 端点）", async () => {
    renderAt(<AppDeployPage />);
    await waitFor(() => expect(screen.getByText("应用一键部署")).toBeTruthy());
    await waitFor(() => expect(screen.getByText("Nginx 网页服务器")).toBeTruthy());
    expect(screen.getByText("已部署应用")).toBeTruthy();
    await waitFor(() => expect(screen.getByText("my-site")).toBeTruthy());
    expect(screen.getByText("healthy")).toBeTruthy();
  });

  it("Docker 页：容器镜像加速器仓库（真连 docker 端点）", async () => {
    renderAt(<DockerPage />);
    await waitFor(() => expect(screen.getAllByText("web-01").length).toBeGreaterThan(0));
    await waitFor(() => expect(screen.getAllByText("nginx:1.25").length).toBeGreaterThan(0));
    expect(screen.getByText("镜像加速器")).toBeTruthy();
    expect(screen.getByText("私有仓库")).toBeTruthy();
    expect(screen.getByText("https://docker.m.daocloud.io")).toBeTruthy();
    expect(screen.getByText("registry.example.com:5000")).toBeTruthy();
  });

  it("网站页：站点列表真连（site 端点）", async () => {
    renderAt(<SitePage />);
    await waitFor(() => expect(screen.getAllByText("my-site").length).toBeGreaterThan(0));
    expect(screen.getByText("example.com")).toBeTruthy();
  });

  it("数据库页：备份清单真连（db/ops 端点）", async () => {
    renderAt(<DbPage />);
    await waitFor(() => expect(screen.getAllByText("mysql-app_prod-20261125030000.sql").length).toBeGreaterThan(0));
    expect(screen.getAllByText("数据库").length).toBeGreaterThan(0);
  });

  it("计划任务页：任务列表真连（cron 端点）", async () => {
    renderAt(<CronPage />);
    await waitFor(() => expect(screen.getAllByText("nightly-backup").length).toBeGreaterThan(0));
    expect(screen.getByText("0 2 * * *")).toBeTruthy();
  });

  it("网站统计页：解析与按日汇总（真连 webstats 端点）", async () => {
    const { container } = renderAt(<WebstatsPage />);
    await waitFor(() => expect(screen.getAllByText("web-01").length).toBeGreaterThan(0));
    await waitFor(() => expect(container.innerHTML.includes("1.2.3.4")).toBe(true));
    expect(screen.getByText("解析访问日志")).toBeTruthy();
    expect(screen.getByText("按日汇总")).toBeTruthy();
    expect(container.innerHTML.includes("/index.html")).toBe(true);
  });

  it("K8s 集群页：集群列表与视图页签（真连 k8s 端点）", async () => {
    renderAt(<K8sPage />);
    await waitFor(() => expect(screen.getByText("K8s 集群")).toBeTruthy());
    await waitFor(() => expect(screen.getByText("prod")).toBeTruthy());
    expect(screen.getAllByText("接入集群").length).toBeGreaterThan(0);
    expect(screen.getAllByText("一键部署").length).toBeGreaterThan(0);
    await waitFor(() => expect(screen.getByText(/集群视图：prod/)).toBeTruthy());
    expect(screen.getAllByText("节点").length).toBeGreaterThan(0);
    expect(screen.getAllByText("工作负载").length).toBeGreaterThan(0);
  });

  it("文件页：目录表与编辑抽屉（真连 files 端点）", async () => {
    const { container } = renderAt(<FilesPage />);
    await waitFor(() => expect(screen.getByText("文件管理")).toBeTruthy());
    expect(screen.getByText(/目录：/)).toBeTruthy();
    await waitFor(() => expect(screen.getByText("nginx.conf")).toBeTruthy());
    expect(/刷\s*新/.test(container.innerHTML)).toBe(true);
  });

  it("日志清理页：规则表与清理操作（真连 logclean 端点）", async () => {
    renderAt(<LogCleanPage />);
    await waitFor(() => expect(screen.getByText("日志清理")).toBeTruthy());
    expect(screen.getByText("清理规则")).toBeTruthy();
    expect(screen.getByText("添加规则")).toBeTruthy();
    expect(screen.getByText("立即清理")).toBeTruthy();
  });

  it("主从复制页：状态卡与配置入口（真连 db 端点）", async () => {
    renderAt(<DbReplicaPage />);
    await waitFor(() => expect(screen.getByText("主从复制")).toBeTruthy());
    expect(screen.getByText("复制状态")).toBeTruthy();
    expect(screen.getByText("主库：创建复制账号")).toBeTruthy();
    expect(screen.getByText("从库：配置复制")).toBeTruthy();
    expect(screen.getByText("数据库外访授权")).toBeTruthy();
    expect(screen.getByText("新增授权")).toBeTruthy();
  });

  it("SSL 证书页：清单表与签发入口（真连 ssl 端点）", async () => {
    renderAt(<SslPage />);
    await waitFor(() => expect(screen.getByText("SSL 证书")).toBeTruthy());
    expect(screen.getByText("证书清单")).toBeTruthy();
    expect(screen.getByText("80 端口预检")).toBeTruthy();
    expect(screen.getByText("签发 Let's Encrypt 证书")).toBeTruthy();
    expect(screen.getByText("上传已有证书")).toBeTruthy();
    expect(screen.getByText("自签证书（离线兜底）")).toBeTruthy();
  });

  it("告警推送页：渠道管理与白名单", async () => {
    renderAt(<PushPage />);
    await waitFor(() => expect(screen.getByText("推送渠道")).toBeTruthy());
    expect(screen.getByText("添加渠道")).toBeTruthy();
    expect(screen.getByText(/SSRF 白名单/)).toBeTruthy();
    expect(screen.getByText("推送日志")).toBeTruthy();
  });
});
