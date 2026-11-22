// Deterministic in-browser mocks for endpoints without an HTTP bridge yet.
// Every mock here is listed in MOCK_ENDPOINTS (client.ts) and in the
// breakpoint-web.md mock manifest. Replacing a mock with a real endpoint
// must remove it from both lists.

export interface NodeInfo {
  nodeId: string;
  hostname: string;
  os: string;
  arch: string;
  version: string;
  tags: string[];
  status: "online" | "offline";
  cpu: number;
  mem: number;
  disk: number;
  load: number;
  lastHeartbeatMs: number;
}

const now = () => Date.now();

export function mockNodes(): NodeInfo[] {
  const t = now();
  return [
    { nodeId: "node-01", hostname: "web-01", os: "ubuntu-22.04", arch: "amd64", version: "0.1.0", tags: ["web"], status: "online", cpu: 23.5, mem: 41.2, disk: 55.1, load: 0.42, lastHeartbeatMs: t - 5000 },
    { nodeId: "node-02", hostname: "db-01", os: "ubuntu-22.04", arch: "amd64", version: "0.1.0", tags: ["db"], status: "online", cpu: 61.0, mem: 72.8, disk: 38.0, load: 1.13, lastHeartbeatMs: t - 8000 },
    { nodeId: "node-03", hostname: "static-assets", os: "debian-12", arch: "arm64", version: "0.1.0", tags: ["cdn"], status: "offline", cpu: 0, mem: 0, disk: 12.4, load: 0, lastHeartbeatMs: t - 3600_000 },
  ];
}

export function mockDispatch(nodeId: string, command: string): { ok: boolean; output: string } {
  if (!mockNodes().some((n) => n.nodeId === nodeId)) {
    return { ok: false, output: `mock: node ${nodeId} not registered` };
  }
  return { ok: true, output: `mock ok: ${command} on ${nodeId} (HTTP->gRPC bridge pending)` };
}

export function mockMetrics(): { ts: number; cpu: number; mem: number; net: number }[] {
  const out: { ts: number; cpu: number; mem: number; net: number }[] = [];
  const t = now();
  for (let i = 23; i >= 0; i--) {
    const h = t - i * 3600_000;
    out.push({ ts: h, cpu: 20 + Math.round(30 * Math.abs(Math.sin(i / 3))), mem: 40 + Math.round(20 * Math.abs(Math.cos(i / 4))), net: Math.round(50 * Math.abs(Math.sin(i / 2))) });
  }
  return out;
}
