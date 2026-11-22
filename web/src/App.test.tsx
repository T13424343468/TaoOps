// Page smoke tests: one per prototype page (17 pages full coverage, v3.16).
// Real-API pages are rendered with fetch mocked; mock-backed pages render as-is.
import { describe, expect, it, vi, beforeAll } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { ConfigProvider } from "antd";
import zhCN from "antd/locale/zh_CN";

import LoginPage from "./pages/Login";
import DashPage from "./pages/Dash";
import WafPage from "./pages/Waf";
import AuditPage from "./pages/Audit";
import TermPage from "./pages/Term";
import ModulePage from "./pages/ModulePage";
import {
  AI_PAGE, CRON_PAGE, DB_PAGE, ENV_PAGE, FILES_PAGE, FTP_PAGE,
  LB_PAGE, MONITOR_PAGE, NODES_PAGE, PUSH_PAGE, SITE_PAGE, SSL_PAGE, UPGRADE_PAGE,
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

describe("17 页全覆盖冒烟", () => {
  it("登录页：登录与初始化两个页签", () => {
    renderAt(<LoginPage onLogin={() => {}} />);
    expect(screen.getByText("TaoOps 控制台")).toBeTruthy();
    expect(screen.getByText("首次初始化")).toBeTruthy();
  });

  it("总览页：节点健康表", () => {
    renderAt(<DashPage />);
    expect(screen.getByText("节点健康")).toBeTruthy();
    expect(screen.getByText("web-01")).toBeTruthy();
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

  const modulePages: [string, React.ReactNode, string][] = [
    ["多机中控", <ModulePage config={NODES_PAGE} />, "批量下发（跨机）"],
    ["网站", <ModulePage config={SITE_PAGE} />, "重载配置"],
    ["数据库", <ModulePage config={DB_PAGE} />, "立即备份"],
    ["文件", <ModulePage config={FILES_PAGE} />, "打包下载"],
    ["FTP", <ModulePage config={FTP_PAGE} />, "重置密码"],
    ["计划任务", <ModulePage config={CRON_PAGE} />, "立即执行"],
    ["环境变量", <ModulePage config={ENV_PAGE} />, "重载环境"],
    ["负载均衡", <ModulePage config={LB_PAGE} />, "摘除节点"],
    ["SSL", <ModulePage config={SSL_PAGE} />, "立即续期"],
    ["监控告警", <ModulePage config={MONITOR_PAGE} />, "静默 1h"],
    ["AI 运维", <ModulePage config={AI_PAGE} />, "生成建议"],
    ["升级", <ModulePage config={UPGRADE_PAGE} />, "执行升级"],
    ["告警推送", <ModulePage config={PUSH_PAGE} />, "测试推送"],
  ];
  for (const [name, el, probe] of modulePages) {
    it(`${name}页：mock 提示与操作按钮`, () => {
      renderAt(el);
      expect(screen.getByText(probe)).toBeTruthy();
    });
  }
});
