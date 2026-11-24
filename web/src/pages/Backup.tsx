import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, Select, Space, Table, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A13 节点间备份：备份列表（真连 backup.manage op=list）、跨节点发送
// （op=send 异步 + send_status 轮询）、中转流量统计（GET /api/relay/stats）。
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

interface RelayStatView {
  relay_id: string;
  target_node_id: string;
  relayed_bytes: number;
  started_ms: number;
  status: string;
}

interface RelayStats {
  total_relayed_bytes: number;
  active_relays: number;
  relays: RelayStatView[];
}

async function cmd(nodeId: string, args: string[]): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/cmd`, {
    method: "POST",
    body: JSON.stringify({ command: "backup.manage", args, timeout_ms: 30000 }),
  });
}

function fmtBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let v = n;
  let u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return `${v.toFixed(1)} ${units[u]}`;
}

export default function BackupPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [backups, setBackups] = useState<{ file: string; info: string }[]>([]);
  const [stats, setStats] = useState<RelayStats | null>(null);
  const [sendStatus, setSendStatus] = useState<string>("");
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();
  const [form] = Form.useForm();

  const loadNodes = useCallback(async () => {
    const list = await api<NodeView[]>("/api/nodes");
    setNodes(list);
    setNodeId((cur) => cur || list[0]?.nodeId || "");
  }, []);

  const loadBackups = useCallback(async (id: string) => {
    if (!id) return;
    try {
      const r = await cmd(id, ["op=list"]);
      const lines = (r.output || "").split("\n").filter((l) => l.trim() && !l.startsWith("("));
      setBackups(lines.map((l) => {
        const [created, file, rest] = l.split(/\s{2,}/);
        return { file: file ?? l, info: [created, rest].filter(Boolean).join("  ") };
      }));
    } catch (e) {
      msg.error(`备份列表加载失败：${(e as Error).message}`);
    }
  }, [msg]);

  const loadStats = useCallback(async () => {
    try { setStats(await api<RelayStats>("/api/relay/stats")); } catch { /* 统计失败不打断页面 */ }
  }, []);

  useEffect(() => { void loadNodes(); void loadStats(); }, [loadNodes, loadStats]);
  useEffect(() => { void loadBackups(nodeId); }, [nodeId, loadBackups]);

  const send = async (values: { file: string; name: string; target: string }) => {
    if (!nodeId) { msg.warning("先选择源节点"); return; }
    if (values.target === nodeId) { msg.warning("目标节点不能与源节点相同"); return; }
    setBusy(true);
    try {
      const r = await cmd(nodeId, [
        "op=send",
        `file=${values.file}`,
        `target_node=${values.target}`,
        `name=${values.name}`,
      ]);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      msg.success("发送已发起，正在中转");
      // 轮询发送状态直至完成或失败（最多 60 次 × 2s）。
      for (let i = 0; i < 60; i++) {
        await new Promise((r2) => setTimeout(r2, 2000));
        const s = await cmd(nodeId, ["op=send_status"]);
        setSendStatus(s.output);
        if (s.output.includes("status=done")) { msg.success("中转完成，目标端已整包校验"); break; }
        if (s.output.includes("status=failed")) { msg.error(`中转失败：${s.output}`); break; }
      }
      void loadStats();
    } catch (e) {
      msg.error(`发送失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>备份还原</Typography.Title>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="节点间备份经中控中转（过账不落盘），分片级 SHA-256 校验、断点续传、整包最终确认；还原需 confirm=true 并先做回滚快照。" />
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ width: 280 }}
          value={nodeId || undefined}
          placeholder="选择节点"
          onChange={setNodeId}
          options={nodes.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
        <Button onClick={() => void loadBackups(nodeId)}>刷新列表</Button>
      </Space>
      <Card size="small" title="备份档案">
        <Table
          rowKey="file"
          size="small"
          pagination={false}
          dataSource={backups}
          columns={[
            { title: "档案", dataIndex: "file" },
            { title: "信息", dataIndex: "info" },
          ]}
          locale={{ emptyText: "（无备份）" }}
        />
      </Card>
      <Card size="small" title="发送到其他节点" style={{ marginTop: 12 }}>
        <Form form={form} layout="inline" onFinish={send}>
          <Form.Item name="name" rules={[{ required: true, message: "备份名必填" }]}>
            <Input placeholder="备份名（如 site-a）" style={{ width: 160 }} />
          </Form.Item>
          <Form.Item name="file" rules={[{ required: true, message: "档案路径必填" }]}>
            <Input placeholder="备份目录内的 .tar.gz 绝对路径" style={{ width: 320 }} />
          </Form.Item>
          <Form.Item name="target" rules={[{ required: true, message: "目标节点必填" }]}>
            <Select
              placeholder="目标节点"
              style={{ width: 220 }}
              options={nodes.filter((n) => n.nodeId !== nodeId).map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
            />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>发起发送</Button>
          </Form.Item>
        </Form>
        {sendStatus && (
          <Typography.Paragraph code style={{ marginTop: 12, marginBottom: 0 }}>{sendStatus}</Typography.Paragraph>
        )}
      </Card>
      <Card size="small" title="中转流量统计" style={{ marginTop: 12 }}>
        <p>
          累计中转 {fmtBytes(stats?.total_relayed_bytes ?? 0)} · 进行中 {stats?.active_relays ?? 0} 条
        </p>
        <Table
          rowKey="relay_id"
          size="small"
          pagination={false}
          dataSource={stats?.relays ?? []}
          columns={[
            { title: "会话", dataIndex: "relay_id" },
            { title: "目标节点", dataIndex: "target_node_id" },
            { title: "已中转", dataIndex: "relayed_bytes", render: (v: number) => fmtBytes(v) },
            {
              title: "状态", dataIndex: "status",
              render: (s: string) => s === "done" ? "完成" : s === "failed" ? "失败" : "进行中",
            },
          ]}
          locale={{ emptyText: "（暂无中转记录）" }}
        />
      </Card>
    </div>
  );
}
