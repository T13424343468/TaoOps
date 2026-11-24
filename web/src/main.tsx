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
import MigratePage from "./pages/Migrate";
import LbPage from "./pages/Lb";
import TotpPage from "./pages/Totp";
import PushPage from "./pages/Push";
import LogCleanPage from "./pages/LogClean";
import DbReplicaPage from "./pages/DbReplica";
import SslPage from "./pages/Ssl";
import FilesPage from "./pages/Files";
import MetricsPage from "./pages/Metrics";
import AppDeployPage from "./pages/AppDeploy";
import K8sPage from "./pages/K8s";
import ModulePage from "./pages/ModulePage";
import {
  AI_PAGE, CRON_PAGE, DB_PAGE, DOCKER_PAGE, ENV_PAGE, FTP_PAGE, WEBSTATS_PAGE,
  NODES_PAGE, SITE_PAGE, UPGRADE_PAGE,
} from "./pages/configs";

const router = createHashRouter([
  { path: "/", element: <AppShell user={null!} onLogout={() => {}} />, children: [
    { path: "dash", element: <DashPage /> },
    { path: "nodes", element: <ModulePage config={NODES_PAGE} /> },
    { path: "term", element: <TermPage /> },
    { path: "monitor", element: <MetricsPage /> },
    { path: "site", element: <ModulePage config={SITE_PAGE} /> },
    { path: "db", element: <ModulePage config={DB_PAGE} /> },
    { path: "files", element: <FilesPage /> },
    { path: "ftp", element: <ModulePage config={FTP_PAGE} /> },
    { path: "cron", element: <ModulePage config={CRON_PAGE} /> },
    { path: "env", element: <ModulePage config={ENV_PAGE} /> },
    { path: "docker", element: <ModulePage config={DOCKER_PAGE} /> },
    { path: "apps", element: <AppDeployPage /> },
    { path: "k8s", element: <K8sPage /> },
    { path: "webstats", element: <ModulePage config={WEBSTATS_PAGE} /> },
    { path: "backup", element: <BackupPage /> },
    { path: "migrate", element: <MigratePage /> },
    { path: "lb", element: <LbPage /> },
    { path: "ssl", element: <SslPage /> },
    { path: "waf", element: <WafPage /> },
    { path: "audit", element: <AuditPage /> },
    { path: "totp", element: <TotpPage /> },
    { path: "ai", element: <ModulePage config={AI_PAGE} /> },
    { path: "upgrade", element: <ModulePage config={UPGRADE_PAGE} /> },
    { path: "push", element: <PushPage /> },
    { path: "logclean", element: <LogCleanPage /> },
    { path: "db-replica", element: <DbReplicaPage /> },
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
