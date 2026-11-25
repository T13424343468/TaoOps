import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, DatePicker, Form, Input, Popconfirm, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";
import TrendChart from "../components/TrendChart";

// 网站统计页（真连 POST /api/nodes/{id}/webstats，契约 FR-A12）。
// 解析访问日志（断点续读）、按日汇总 PV/UV/状态码/流量/Top、导出 JSON、清空。
// UV 为 HLL 估算（标准误差 ~1.6%）；日志缺 $body_bytes_sent 时流量降级仅计 PV 并标注。

interface NodeView {
  nodeId: string;
  hostname: string;
  commandConnected: boolean;
}

interface CmdOutcome {
  exit_code: number;
  output: string;
  request_id: string;
}

interface ParseSummary {
  lines: number;
  parsed: number;
  skipped: number;
  days: string[];
  resumed: boolean;
}

interface TopEntry {
  key: string;
  count: number;
}

interface DayRow {
  date: string;
  site: string;
  pv: number;
  uv: number;
  status: Record<string, number>;
  bytes: number;
  degraded: boolean;
  top_ips: TopEntry[];
  top_urls: TopEntry[];
}

async function webstatsOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/webstats`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 60000, ...req }),
  });
}

const fmtBytes = (n: number): string => {
  if (!n) return "-";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let v = n;
  let u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u += 1; }
  return `${v.toFixed(v >= 100 || u === 0 ? 0 : 1)} ${units[u]}`;
};

const fmtDate = (d: Date): string => d.toISOString().slice(0, 10);

export default function WebstatsPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [rows, setRows] = useState<DayRow[]>([]);
  const [lastParse, setLastParse] = useState<ParseSummary | null>(null);
  const [busy, setBusy] = useState(false);
  const [range, setRange] = useState<{ from: string; to: string }>({ from: "", to: "" });
  const [parseForm] = Form.useForm();
  const [msg, contextHolder] = message.useMessage();

  const loadNodes = useCallback(async () => {
    const list = await api<NodeView[]>("/api/nodes");
    setNodes(list);
    setNodeId((cur) => cur || list[0]?.nodeId || "");
  }, []);

  const loadSummary = useCallback(async (id: string) => {
    if (!id) return;
    try {
      const r = await webstatsOp(id, { op: "summary", from: range.from, to: range.to });
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      try { setRows(JSON.parse(r.output) as DayRow[]); }
      catch { setRows([]); }
    } catch (e) {
      msg.error(`加载统计失败：${(e as Error).message}`);
    }
  }, [range.from, range.to, msg]);

  useEffect(() => { void loadNodes(); }, [loadNodes]);
  useEffect(() => { void loadSummary(nodeId); }, [nodeId, loadSummary]);

  const run = async (req: Record<string, unknown>, ok: string) => {
    if (!nodeId) { msg.warning("先选择节点"); return null; }
    setBusy(true);
    try {
      const r = await webstatsOp(nodeId, req);
      if (r.exit_code !== 0) { msg.error(r.output); return null; }
      msg.success(ok);
      return r;
    } catch (e) {
      msg.error(`操作失败：${(e as Error).message}`);
      return null;
    } finally {
      setBusy(false);
    }
  };

  const parse = async (values: { site: string; path: string }) => {
    const r = await run({ op: "parse", site: values.site, path: values.path }, "解析完成");
    if (!r) return;
    try { setLastParse(JSON.parse(r.output) as ParseSummary); }
    catch { setLastParse(null); }
    await loadSummary(nodeId);
  };

  const exportJson = async (values: { site?: string }) => {
    const r = await run({ op: "export", site: values.site ?? "", from: range.from, to: range.to }, "已导出");
    if (!r) return;
    const blob = new Blob([r.output], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `webstats-export-${fmtDate(new Date())}.json`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const clear = async (values: { site?: string }) => {
    await run({ op: "clear", site: values.site ?? "" }, "已清空");
    await loadSummary(nodeId);
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>网站统计</Typography.Title>
      <Space style={{ marginBottom: 12 }} wrap>
        <Select
          style={{ minWidth: 220 }}
          value={nodeId || undefined}
          onChange={(v) => { setNodeId(v); setLastParse(null); }}
          placeholder="选择节点"
          options={nodes.map((n) => ({ value: n.nodeId, label: n.hostname }))}
        />
        <DatePicker.RangePicker
          onChange={(dates) => {
            setRange(dates?.[0] && dates?.[1]
              ? { from: fmtDate(dates[0].toDate()), to: fmtDate(dates[1].toDate()) }
              : { from: "", to: "" });
          }}
        />
        <Button disabled={busy} onClick={() => void loadSummary(nodeId)}>刷新</Button>
      </Space>

      <Card size="small" title="解析访问日志" style={{ marginBottom: 12 }}>
        <Form form={parseForm} layout="inline" onFinish={parse} style={{ flexWrap: "wrap", rowGap: 8 }}>
          <Form.Item name="site" rules={[{ required: true, message: "填站点名" }]}>
            <Input style={{ width: 180 }} placeholder="站点名，如 my-site" />
          </Form.Item>
          <Form.Item name="path" rules={[{ required: true, message: "填日志路径" }]}>
            <Input style={{ width: 360 }} placeholder="日志绝对路径，如 /var/log/nginx/access.log" />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>解析</Button>
          </Form.Item>
        </Form>
        {lastParse && (
          <Alert
            type="success"
            showIcon
            style={{ marginTop: 8 }}
            message={`共 ${lastParse.lines} 行：解析 ${lastParse.parsed}，跳过 ${lastParse.skipped}；覆盖 ${lastParse.days.length} 天${lastParse.resumed ? "（断点续读）" : ""}`}
          />
        )}
      </Card>

      <Card size="small" title="按日汇总" style={{ marginBottom: 12 }}>
        {rows.length > 0 && (
          <div style={{ marginBottom: 12 }}>
            <TrendChart points={rows.map((r) => ({ date: r.date, pv: r.pv, uv: r.uv }))} />
          </div>
        )}
        <Table
          rowKey={(r: DayRow) => r.site + r.date}
          size="small"
          pagination={{ pageSize: 15 }}
          dataSource={rows}
          columns={[
            { title: "站点", dataIndex: "site" },
            { title: "日期", dataIndex: "date" },
            { title: "PV", dataIndex: "pv" },
            { title: "UV（估算）", dataIndex: "uv" },
            {
              title: "状态码",
              dataIndex: "status",
              render: (v: Record<string, number>) => Object.entries(v).map(([k, n]) => (
                <Tag key={k} color={k.startsWith("2") ? "green" : k.startsWith("4") || k.startsWith("5") ? "red" : "blue"}>{`${k}×${n}`}</Tag>
              )),
            },
            { title: "流量", dataIndex: "bytes", render: (v: number) => fmtBytes(v) },
            {
              title: "备注",
              dataIndex: "degraded",
              render: (v: boolean) => (v ? <Tag color="orange">日志缺字节数，流量仅 PV</Tag> : null),
            },
            {
              title: "Top IP / URL",
              render: (_: unknown, r: DayRow) => (
                <Space size={4} wrap>
                  {r.top_ips.slice(0, 3).map((t) => <Tag key={`ip-${t.key}`}>{`IP ${t.key}×${t.count}`}</Tag>)}
                  {r.top_urls.slice(0, 3).map((t) => <Tag key={`url-${t.key}`}>{t.key}×{t.count}</Tag>)}
                </Space>
              ),
            },
          ]}
        />
      </Card>

      <Card size="small" title="导出与清空">
        <Form layout="inline" style={{ flexWrap: "wrap", rowGap: 8 }}>
          <Form.Item name="site" label="站点（留空为全部）">
            <Input style={{ width: 200 }} placeholder="站点名" />
          </Form.Item>
        </Form>
        <Space wrap style={{ marginTop: 8 }}>
          <Button disabled={busy} onClick={() => void exportJson({})}>导出 JSON</Button>
          <Popconfirm title="清空统计会删除聚合数据（含断点），确认？" onConfirm={() => void clear({})}>
            <Button danger disabled={busy}>清空全部统计</Button>
          </Popconfirm>
        </Space>
      </Card>
    </div>
  );
}
