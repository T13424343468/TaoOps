// API client: real endpoints + explicit mock registry.
// Real endpoints (server/Program.cs): /api/auth/*, /api/waf/*, /api/audit*, /api/sec/events,
// WebSocket /ws/agent/{nodeId}/terminal|logs.
// Mock endpoints (no HTTP bridge yet, see MOCK_ENDPOINTS below): node registry,
// node command dispatch (sysinfo/site/db/files/cron/env/fw/backup/ftp), AI ops,
// upgrade, lb, ssl, push, monitor metrics.

export const MOCK_ENDPOINTS: string[] = [
  "GET /api/nodes",
  "POST /api/nodes/{id}/cmd (sysinfo.overview/site.manage/db.manage/files.manage/cron.manage/env.manage/fw.manage/backup.manage/ftp.manage)",
  "GET /api/monitor/metrics (24h cpu/mem/net)",
  "GET /api/ai/* (insight/scan)",
  "GET|POST /api/upgrade/* (channel/packages/queue)",
  "GET|POST /api/lb/* (upstreams)",
  "GET|POST /api/ssl/* (certs/renew)",
  "GET|POST /api/push/* (rules/channels/silence)",
];

export interface AuthUser {
  username: string;
  role: string;
}

function token(): string | null {
  return localStorage.getItem("taoops_token");
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  const t = token();
  if (t) headers.set("Authorization", `Bearer ${t}`);
  if (init?.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  const res = await fetch(path, { ...init, headers });
  if (res.status === 401) {
    localStorage.removeItem("taoops_token");
    throw new Error("unauthorized");
  }
  if (!res.ok) {
    let msg = `${res.status}`;
    try {
      const body = await res.json();
      if (body?.error) msg = body.error;
    } catch { /* keep status */ }
    throw new Error(msg);
  }
  if (res.status === 204) return undefined as T;
  const ct = res.headers.get("content-type") ?? "";
  return (ct.includes("json") ? res.json() : res.text()) as Promise<T>;
}

// ---- auth ----
/** 密码正确但已启用 TOTP：需二次校验（FR-A16）。 */
export class TotpRequiredError extends Error {
  constructor() { super("totp_required"); }
}

export async function login(username: string, password: string): Promise<AuthUser> {
  const r = await api<{ token?: string; username?: string; role?: string; error?: string; totp_required?: boolean }>(
    "/api/auth/login",
    { method: "POST", body: JSON.stringify({ username, password }) },
  );
  if (r.totp_required) throw new TotpRequiredError();
  if (!r.token) throw new Error(r.error ?? "login failed");
  localStorage.setItem("taoops_token", r.token);
  return { username: r.username ?? username, role: r.role ?? "Operator" };
}

/** TOTP 登录二次校验（6 位动态码或恢复码），成功签发会话。 */
export async function totpVerify(username: string, code: string): Promise<AuthUser> {
  const r = await api<{ token?: string; username?: string; role?: string; error?: string }>(
    "/api/auth/totp/verify",
    { method: "POST", body: JSON.stringify({ username, code }) },
  );
  if (!r.token) throw new Error(r.error ?? "totp verify failed");
  localStorage.setItem("taoops_token", r.token);
  return { username: r.username ?? username, role: r.role ?? "Operator" };
}

export async function totpStatus(username: string): Promise<{ enabled: boolean }> {
  return api(`/api/auth/totp/status?username=${encodeURIComponent(username)}`);
}

export async function totpSetup(username: string, password: string): Promise<{ secret: string; otpauth_uri: string }> {
  return api("/api/auth/totp/setup", { method: "POST", body: JSON.stringify({ username, password }) });
}

export async function totpEnable(username: string, code: string, password: string): Promise<{ recovery_codes: string[] }> {
  return api("/api/auth/totp/enable", { method: "POST", body: JSON.stringify({ username, code, password }) });
}

export async function totpRegenerate(username: string, password: string): Promise<{ recovery_codes: string[] }> {
  return api("/api/auth/totp/recovery/regenerate", { method: "POST", body: JSON.stringify({ username, password }) });
}

export async function bootstrap(username: string, password: string): Promise<void> {
  await api("/api/auth/bootstrap", { method: "POST", body: JSON.stringify({ username, password }) });
}

export async function me(): Promise<AuthUser> {
  return api<AuthUser>("/api/auth/me");
}

export async function logout(): Promise<void> {
  try { await api("/api/auth/logout", { method: "POST" }); } finally {
    localStorage.removeItem("taoops_token");
  }
}

export function wsUrl(nodeId: string, kind: "terminal" | "logs", query = ""): string {
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const t = token();
  const q = new URLSearchParams(query);
  if (t) q.set("token", t);
  return `${proto}://${location.host}/ws/agent/${nodeId}/${kind}?${q.toString()}`;
}
