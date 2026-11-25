import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, InputNumber, Popconfirm, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A13 节点间备份：备份列表（真连 backup.manage op=list）、跨节点发送
// （op=send 异步 + send_status 轮询）、中转流量统计（GET /api/relay/stats）。
// FR-A26 定时备份策略：策略表（/backup/policy 端点，policy_list/add/remove/run），
// 中控调度器每 60s 巡检到期策略自动执行，失败接告警中心。
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

interface BackupPolicy {
  id: string;
  name: string;
  source: string;
  type: string;
  schedule: string;
  retain: number;
  enabled: boolean;
  last_run_ms: number;
  last_result: string;
}

async function policyOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/backup/policy`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 30000, ...req }),
  });
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
  const [policies, setPolicies] = useState<BackupPolicy[]>([]);
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();
  const [form] = Form.useForm();
  const [policyForm] = Form.useForm();

  const loadPolicies = useCallback(async (id: string) => {
    if (!id) return;
    try {
      const r = await policyOp(id, { op: "policy_list" });
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      try { setPolicies(JSON.parse(r.output)); } catch { setPolicies([]); }
    } catch (e) {
      msg.error(`策略加载失败：${(e as Error).message}`);
    }
  }, [msg]);

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
  useEffect(() => { void loadBackups(nodeId); void loadPolicies(nodeId); }, [nodeId, loadBackups, loadPolicies]);

  const runPolicyOp = async (req: Record<string, unknown>, ok: string) => {
    if (!nodeId) { msg.warning("先选择节点"); return; }
    setBusy(true);
    try {
      const r = await policyOp(nodeId, req);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      msg.success(ok);
      await loadPolicies(nodeId);
      await loadBackups(nodeId);
    } catch (e) {
      msg.error(`操作失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const addPolicy = async (values: { name: string; source: string; type: string; schedule: string; retain?: number }) => {
    await runPolicyOp({
      op: "policy_add", name: values.name, source: values.source,
      type: values.type || "full", schedule: values.schedule, retain: values.retain ?? 7,
    }, "策略已添加（中控调度器每分钟巡检到期自动执行）");
    policyForm.resetFields();
  };

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
      <Card size="small" title="定时备份策略" style={{ marginTop: 12 }}>
        <Table
          rowKey="id"
          size="small"
          pagination={false}
          dataSource={policies}
          locale={{ emptyText: "暂无策略，先在下方添加" }}
          columns={[
            { title: "名称", dataIndex: "name" },
            { title: "来源", dataIndex: "source" },
            { title: "类型", dataIndex: "type" },
            { title: "计划", dataIndex: "schedule" },
            { title: "保留", dataIndex: "retain", render: (v: number) => `${v} 份` },
            {
              title: "状态",
              render: (_: unknown, p: BackupPolicy) => (p.enabled ? <Tag color="green">启用</Tag> : <Tag>停用</Tag>),
            },
            {
              title: "最近执行",
              render: (_: unknown, p: BackupPolicy) =>
                p.last_run_ms > 0
                  ? `${new Date(p.last_run_ms).toLocaleString()} · ${p.last_result}`
                  : "未执行",
            },
            {
              title: "操作",
              render: (_: unknown, p: BackupPolicy) => (
                <Space>
                  <Popconfirm title="立即执行该策略？" onConfirm={() => void runPolicyOp({ op: "policy_run", id: p.id }, "策略已触发执行")}>
                    <Button size="small" loading={busy}>立即执行</Button>
                  </Popconfirm>
                  <Popconfirm title="删除该策略？" onConfirm={() => void runPolicyOp({ op: "policy_remove", id: p.id }, "策略已删除")}>
                    <Button size="small" danger loading={busy}>删除</Button>
                  </Popconfirm>
                </Space>
              ),
            },
          ]}
        />
        <Form form={policyForm} layout="inline" onFinish={addPolicy} style={{ marginTop: 12 }}>
          <Form.Item name="name" rules={[{ required: true, message: "策略名必填" }]}>
            <Input placeholder="策略名（如 site-a）" style={{ width: 150 }} />
          </Form.Item>
          <Form.Item name="source" rules={[{ required: true, message: "来源路径必填" }]}>
            <Input placeholder="备份来源绝对路径" style={{ width: 240 }} />
          </Form.Item>
          <Form.Item name="type" initialValue="full">
            <Select style={{ width: 110 }} options={[{ value: "full", label: "全量" }, { value: "incremental", label: "增量" }]} />
          </Form.Item>
          <Form.Item name="schedule" rules={[{ required: true, message: "计划必填（interval:秒 或 daily:HH:MM）" }]}>
            <Input placeholder="interval:3600 或 daily:03:00" style={{ width: 200 }} />
          </Form.Item>
          <Form.Item name="retain" initialValue={7}>
            <InputNumber min={1} placeholder="保留份数" style={{ width: 110 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>添加策略</Button>
          </Form.Item>
        </Form>
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
