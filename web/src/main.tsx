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
import DockerPage from "./pages/Docker";
import WebstatsPage from "./pages/Webstats";
import CronPage from "./pages/Cron";
import SitePage from "./pages/Site";
import DbPage from "./pages/Db";
import FtpPage from "./pages/Ftp";
import EnvPage from "./pages/Env";
import UpgradePage from "./pages/Upgrade";
import UsersPage from "./pages/Users";
import FwPage from "./pages/Fw";
import SettingsPage from "./pages/Settings";
import ModulePage from "./pages/ModulePage";
import {
  AI_PAGE,
  // DB_PAGE、FTP_PAGE、ENV_PAGE、UPGRADE_PAGE 已切真（backlog B2）
  NODES_PAGE,
} from "./pages/configs";

const router = createHashRouter([
  { path: "/", element: <AppShell user={null!} onLogout={() => {}} />, children: [
    { path: "dash", element: <DashPage /> },
    { path: "nodes", element: <ModulePage config={NODES_PAGE} /> },
    { path: "term", element: <TermPage /> },
    { path: "monitor", element: <MetricsPage /> },
    { path: "site", element: <SitePage /> },
    { path: "db", element: <DbPage /> },
    { path: "files", element: <FilesPage /> },
    { path: "ftp", element: <FtpPage /> },
    { path: "cron", element: <CronPage /> },
    { path: "env", element: <EnvPage /> },
    { path: "docker", element: <DockerPage /> },
    { path: "apps", element: <AppDeployPage /> },
    { path: "k8s", element: <K8sPage /> },
    { path: "webstats", element: <WebstatsPage /> },
    { path: "backup", element: <BackupPage /> },
    { path: "migrate", element: <MigratePage /> },
    { path: "lb", element: <LbPage /> },
    { path: "ssl", element: <SslPage /> },
    { path: "waf", element: <WafPage /> },
    { path: "fw", element: <FwPage /> },
    { path: "users", element: <UsersPage /> },
    { path: "settings", element: <SettingsPage /> },
    { path: "audit", element: <AuditPage /> },
    { path: "totp", element: <TotpPage /> },
    { path: "ai", element: <ModulePage config={AI_PAGE} /> },
    { path: "upgrade", element: <UpgradePage /> },
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
