import { useCallback, useEffect, useRef, useState } from "react";
import { Card, Radio, Select, Space, Table, Typography } from "antd";
import * as echarts from "echarts";
import { api } from "../api/client";

// FR-A23 资源实时监控：节点详情实时曲线（真连 GET /api/nodes/{id}/metrics，
// 中控 60s 采样入内存环形缓冲，近 1h/24h 切换）与进程 Top10 表
// （真连 sysinfo.overview op=process_top）。
interface NodeView {
  nodeId: string;
  hostname: string;
  commandConnected: boolean;
}

interface MetricPoint {
  ts: number;
  cpu_used_pct: number;
  mem_total_kb: number;
  mem_used_kb: number;
  disk_read_bps: number;
  disk_write_bps: number;
  net_rx_total: number;
  net_tx_total: number;
}

interface ProcTopRow {
  pid: number;
  name: string;
  cpu_pct: number;
  mem_kb: number;
}

interface CmdOutcome {
  exit_code: number;
  output: string;
  request_id: string;
}

function fmtMem(kb: number): string {
  if (kb < 1024) return `${kb} KB`;
  const units = ["MB", "GB", "TB"];
  let v = kb / 1024;
  let u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return `${v.toFixed(1)} ${units[u]}`;
}

export default function MetricsPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [hours, setHours] = useState<1 | 24>(1);
  const [points, setPoints] = useState<MetricPoint[]>([]);
  const [procs, setProcs] = useState<ProcTopRow[]>([]);
  const chartRef = useRef<HTMLDivElement | null>(null);
  const chart = useRef<echarts.ECharts | null>(null);

  const loadNodes = useCallback(async () => {
    try {
      const list = await api<NodeView[]>("/api/nodes");
      setNodes(list);
      setNodeId((cur) => cur || list[0]?.nodeId || "");
    } catch { setNodes([]); }
  }, []);

  const loadMetrics = useCallback(async (id: string, h: 1 | 24) => {
    if (!id) return;
    try { const rows = await api<MetricPoint[]>(`/api/nodes/${encodeURIComponent(id)}/metrics?hours=${h}`); setPoints(Array.isArray(rows) ? rows : []); }
    catch { setPoints([]); }
  }, []);

  const loadProcs = useCallback(async (id: string) => {
    if (!id) return;
    try {
      const r = await api<CmdOutcome>(`/api/nodes/${encodeURIComponent(id)}/cmd`, {
        method: "POST",
        body: JSON.stringify({ command: "sysinfo.overview", args: ["op=process_top", "n=10"], timeout_ms: 30000 }),
      });
      if (r.exit_code !== 0) { setProcs([]); return; }
      try { const rows = JSON.parse(r.output); setProcs(Array.isArray(rows) ? rows : []); } catch { setProcs([]); }
    } catch { setProcs([]); }
  }, []);

  useEffect(() => { void loadNodes(); }, [loadNodes]);
  useEffect(() => { void loadMetrics(nodeId, hours); void loadProcs(nodeId); }, [nodeId, hours, loadMetrics, loadProcs]);

  // 节点详情曲线：CPU% 与内存%两条线。容器无宽度（测试环境/隐藏）时跳过初始化。
  useEffect(() => {
    if (!chartRef.current || chart.current) return;
    if (chartRef.current.clientWidth === 0) return;
    chart.current = echarts.init(chartRef.current, undefined, { renderer: "svg" });
    const onResize = () => chart.current?.resize();
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("resize", onResize);
      chart.current?.dispose();
      chart.current = null;
    };
  }, []);

  useEffect(() => {
    const c = chart.current;
    if (!c) return;
    const xs = points.map((p) => new Date(p.ts).toLocaleTimeString());
    const memPct = points.map((p) => (p.mem_total_kb > 0 ? (p.mem_used_kb / p.mem_total_kb) * 100 : 0));
    c.setOption({
      tooltip: { trigger: "axis" },
      legend: { data: ["CPU%", "内存%"] },
      grid: { left: 48, right: 24, top: 36, bottom: 28 },
      xAxis: { type: "category", data: xs },
      yAxis: { type: "value", max: 100, axisLabel: { formatter: "{value}%" } },
      series: [
        { name: "CPU%", type: "line", data: points.map((p) => Number(p.cpu_used_pct.toFixed(1))), showSymbol: false },
        { name: "内存%", type: "line", data: memPct.map((v) => Number(v.toFixed(1))), showSymbol: false },
      ],
    });
  }, [points]);

  const latest = points.length > 0 ? points[points.length - 1] : null;

  return (
    <div>
      <Typography.Title level={4}>资源监控</Typography.Title>
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ width: 280 }}
          value={nodeId || undefined}
          placeholder="选择节点"
          onChange={setNodeId}
          options={nodes.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
        <Radio.Group value={hours} onChange={(e) => setHours(e.target.value as 1 | 24)} optionType="button" buttonStyle="solid">
          <Radio.Button value={1}>近 1 小时</Radio.Button>
          <Radio.Button value={24}>近 24 小时</Radio.Button>
        </Radio.Group>
      </Space>
      <Card size="small" title={latest ? `CPU ${latest.cpu_used_pct.toFixed(1)}% · 内存 ${fmtMem(latest.mem_used_kb)} / ${fmtMem(latest.mem_total_kb)}` : "暂无采样数据（中控每 60s 采集一次）"}>
        <div ref={chartRef} style={{ width: "100%", height: 320 }} />
      </Card>
      <Card size="small" title="进程 Top10（按 CPU）" style={{ marginTop: 12 }}>
        <Table
          rowKey="pid"
          size="small"
          pagination={false}
          dataSource={procs}
          locale={{ emptyText: "（暂无数据）" }}
          columns={[
            { title: "PID", dataIndex: "pid" },
            { title: "进程", dataIndex: "name" },
            { title: "CPU (ticks)", dataIndex: "cpu_pct", render: (v: number) => v.toFixed(0) },
            { title: "内存", dataIndex: "mem_kb", render: (v: number) => fmtMem(v) },
          ]}
        />
      </Card>
    </div>
  );
}
