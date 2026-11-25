import { useCallback, useEffect, useState } from "react";
import { Button, Card, Form, InputNumber, Popconfirm, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// 主机防火墙与防篡改页（backlog F3）：真连 POST /api/nodes/{id}/fw 与 /api/nodes/{id}/tamperguard。
// fw：audit 只读，allow/deny/remove/rollback 限 Operator+（后端 FwPolicy 分级）。
// tamperguard：status 只读。结构化结果按 FR-A17 约定落 Output JSON。

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

interface FwEntry {
  time: string;
  action: string; // allow | deny | remove
  port: number;
  proto: string;
  rollback: string;
}

interface TamperStatus {
  mode: string;
  paths: string[];
  events_written: number;
}

async function fwOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/fw`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 60000, ...req }),
  });
}

async function tgOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/tamperguard`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 60000, ...req }),
  });
}

// audit 的结构化结果落 Output JSON（entries 数组）。
function parseEntries(output: string): FwEntry[] {
  try {
    const parsed = JSON.parse(output);
    return Array.isArray(parsed?.entries) ? parsed.entries : [];
  } catch {
    return [];
  }
}

function parseStatus(output: string): TamperStatus | null {
  try {
    const parsed = JSON.parse(output);
    return parsed && typeof parsed === "object" && "mode" in parsed ? parsed : null;
  } catch {
    return null;
  }
}

const actionTag: Record<string, React.ReactNode> = {
  allow: <Tag color="green">放行</Tag>,
  deny: <Tag color="red">封禁</Tag>,
  remove: <Tag>删除规则</Tag>,
};

export default function Fw() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [entries, setEntries] = useState<FwEntry[]>([]);
  const [tg, setTg] = useState<TamperStatus | null>(null);
  const [loading, setLoading] = useState(false);
  const [form] = Form.useForm();
  const [msg, contextHolder] = message.useMessage();

  useEffect(() => {
    api<NodeView[]>("/api/nodes").then((list) => {
      setNodes(list);
      const first = list.find((n) => n.commandConnected) ?? list[0];
      if (first) setNodeId(first.nodeId);
    }).catch(() => msg.error("节点列表加载失败"));
  }, [msg]);

  const refresh = useCallback(async () => {
    if (!nodeId) return;
    setLoading(true);
    try {
      const [fwRes, tgRes] = await Promise.all([
        fwOp(nodeId, { op: "audit" }),
        tgOp(nodeId, { op: "status" }),
      ]);
      setEntries(parseEntries(fwRes.output));
      setTg(parseStatus(tgRes.output));
    } catch (e) {
      msg.error(`加载失败：${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  }, [nodeId, msg]);

  useEffect(() => { void refresh(); }, [refresh]);

  const runFw = async (req: Record<string, unknown>, okText: string) => {
    try {
      const res = await fwOp(nodeId, req);
      if (res.exit_code !== 0) throw new Error(res.output || `exit ${res.exit_code}`);
      msg.success(okText);
      await refresh();
    } catch (e) {
      msg.error(`操作失败：${(e as Error).message}`);
    }
  };

  const onRule = async () => {
    const v = await form.validateFields();
    await runFw({ op: v.op, port: v.port, proto: v.proto }, "规则已下发");
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>主机防火墙与防篡改</Typography.Title>
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ minWidth: 240 }}
          value={nodeId || undefined}
          onChange={(v: string) => setNodeId(v)}
          options={nodes.map((n) => ({
            value: n.nodeId,
            label: `${n.hostname || n.nodeId}${n.commandConnected ? "" : "（离线）"}`,
          }))}
          placeholder="选择节点"
        />
        <Button onClick={() => void refresh()} loading={loading}>刷新</Button>
      </Space>

      <Card size="small" title="防火墙规则变更" style={{ marginBottom: 16 }}>
        <Form form={form} layout="inline" initialValues={{ op: "allow", proto: "tcp" }}>
          <Form.Item name="op" label="操作" rules={[{ required: true }]}>
            <Select style={{ width: 120 }} options={[
              { value: "allow", label: "放行端口" },
              { value: "deny", label: "封禁端口" },
              { value: "remove", label: "删除规则" },
            ]} />
          </Form.Item>
          <Form.Item name="port" label="端口" rules={[{ required: true, message: "请输入端口" }]}>
            <InputNumber min={1} max={65535} style={{ width: 110 }} />
          </Form.Item>
          <Form.Item name="proto" label="协议" rules={[{ required: true }]}>
            <Select style={{ width: 90 }} options={[
              { value: "tcp", label: "TCP" },
              { value: "udp", label: "UDP" },
            ]} />
          </Form.Item>
          <Form.Item>
            <Popconfirm title="确认下发该防火墙操作？" okText="下发" onConfirm={() => void onRule()}>
              <Button type="primary">下发</Button>
            </Popconfirm>
          </Form.Item>
          <Form.Item>
            <Popconfirm title="回滚最近一次变更？" okText="回滚" onConfirm={() => void runFw({ op: "rollback" }, "已回滚")}>
              <Button danger>回滚上次</Button>
            </Popconfirm>
          </Form.Item>
        </Form>
      </Card>

      <Card size="small" title="变更审计（可回滚记录）" style={{ marginBottom: 16 }}>
        <Table
          rowKey={(r: FwEntry) => `${r.time}-${r.action}-${r.port}-${r.proto}`}
          size="small"
          pagination={false}
          loading={loading}
          dataSource={entries}
          columns={[
            { title: "时间", dataIndex: "time" },
            { title: "操作", dataIndex: "action", render: (a: string) => actionTag[a] ?? a },
            { title: "端口", dataIndex: "port" },
            { title: "协议", dataIndex: "proto", render: (p: string) => p.toUpperCase() },
            { title: "回滚命令", dataIndex: "rollback", ellipsis: true, render: (c: string) => <code>{c}</code> },
          ]}
        />
      </Card>

      <Card size="small" title="防篡改状态">
        {tg ? (
          <Table
            rowKey={(_) => "tg"}
            size="small"
            pagination={false}
            dataSource={[tg]}
            columns={[
              { title: "模式", dataIndex: "mode", render: (m: string) => <Tag color={m && m !== "off" ? "green" : "default"}>{m || "—"}</Tag> },
              { title: "监控路径", dataIndex: "paths", render: (paths: string[]) => (paths?.length ? paths.join("；") : "—") },
              { title: "已记录事件数", dataIndex: "events_written" },
            ]}
          />
        ) : (
          <Typography.Text type="secondary">{loading ? "加载中…" : "暂无状态（节点未启用防篡改或加载失败）"}</Typography.Text>
        )}
      </Card>
    </div>
  );
}
