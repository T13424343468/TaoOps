import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createHashRouter, RouterProvider } from "react-router-dom";
import { ConfigProvider } from "antd";
import zhCN from "antd/locale/zh_CN";
import AppShell, { useAuthGate } from "./App";
import LoginPage from "./pages/Login";
import DashPage from "./pages/Dash";
import WafPage from "./pages/Waf";
import AuditPage from "./pages/Audit";
import TermPage from "./pages/Term";
import BackupPage from "./pages/Backup";
import ModulePage from "./pages/ModulePage";
import {
  AI_PAGE, CRON_PAGE, DB_PAGE, DOCKER_PAGE, ENV_PAGE, FILES_PAGE, FTP_PAGE, WEBSTATS_PAGE,
  LB_PAGE, MONITOR_PAGE, NODES_PAGE, PUSH_PAGE, SITE_PAGE, SSL_PAGE, UPGRADE_PAGE,
} from "./pages/configs";

const router = createHashRouter([
  { path: "/", element: <AppShell user={null!} onLogout={() => {}} />, children: [
    { path: "dash", element: <DashPage /> },
    { path: "nodes", element: <ModulePage config={NODES_PAGE} /> },
    { path: "term", element: <TermPage /> },
    { path: "monitor", element: <ModulePage config={MONITOR_PAGE} /> },
    { path: "site", element: <ModulePage config={SITE_PAGE} /> },
    { path: "db", element: <ModulePage config={DB_PAGE} /> },
    { path: "files", element: <ModulePage config={FILES_PAGE} /> },
    { path: "ftp", element: <ModulePage config={FTP_PAGE} /> },
    { path: "cron", element: <ModulePage config={CRON_PAGE} /> },
    { path: "env", element: <ModulePage config={ENV_PAGE} /> },
    { path: "docker", element: <ModulePage config={DOCKER_PAGE} /> },
    { path: "webstats", element: <ModulePage config={WEBSTATS_PAGE} /> },
    { path: "backup", element: <BackupPage /> },
    { path: "lb", element: <ModulePage config={LB_PAGE} /> },
    { path: "ssl", element: <ModulePage config={SSL_PAGE} /> },
    { path: "waf", element: <WafPage /> },
    { path: "audit", element: <AuditPage /> },
    { path: "ai", element: <ModulePage config={AI_PAGE} /> },
    { path: "upgrade", element: <ModulePage config={UPGRADE_PAGE} /> },
    { path: "push", element: <ModulePage config={PUSH_PAGE} /> },
  ]},
]);

// Gate: login page until /api/auth/me succeeds; AppShell needs the real user.
function Root() {
  const { state, user, refresh } = useAuthGate();
  if (state === "loading") return null;
  if (state === "anon" || !user) return <LoginPage onLogin={refresh} />;
  return (
    <ConfigProvider locale={zhCN}>
      <RouterProvider router={createHashRouter([
        { path: "/", element: <AppShell user={user} onLogout={refresh} />, children: router.routes[0].children },
      ])} />
    </ConfigProvider>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
);
