import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, InputNumber, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A18 负载均衡：Nginx upstream 组管理（真连 POST /api/nodes/{id}/lb，
// 契约 §9）。list 看组与健康状态；add 加后端（host 正则/端口/权重校验在
// Agent 侧 nginx -t 落盘前把关）；drain/undrain 摘除与恢复；health 手动
// 触发一轮 TCP/HTTP 探测（间隔 30s、连续 3 次失败自动判下线）。
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

interface LbBackend {
  host: string;
  port: number;
  weight: number;
  backup: boolean;
  down: boolean;
  health_mode: string;
  health_path?: string;
  fail_count: number;
}

interface LbGroup {
  name: string;
  backends: LbBackend[];
}

async function lbOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/lb`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 30000, ...req }),
  });
}

export default function LbPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [groups, setGroups] = useState<LbGroup[]>([]);
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();

  const loadNodes = useCallback(async () => {
    const list = await api<NodeView[]>("/api/nodes");
    setNodes(list);
    setNodeId((cur) => cur || list[0]?.nodeId || "");
  }, []);

  const loadGroups = useCallback(async (id: string) => {
    if (!id) return;
    try {
      const r = await lbOp(id, { op: "list" });
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      // 优先解析 Output JSON（FR-A17：下发桥仅回传 Output）；兼容旧 data.groups 字段。
      let raw: LbGroup[] | undefined;
      try {
        const parsed = JSON.parse(r.output) as { groups?: LbGroup[] };
        raw = parsed.groups;
      } catch {
        raw = (r as unknown as { groups?: LbGroup[] }).groups;
      }
      setGroups(Array.isArray(raw) ? raw : []);
    } catch (e) {
      msg.error(`加载上游组失败：${(e as Error).message}`);
    }
  }, [msg]);

  useEffect(() => { void loadNodes(); }, [loadNodes]);
  useEffect(() => { void loadGroups(nodeId); }, [nodeId, loadGroups]);

  const run = async (req: Record<string, unknown>, ok: string) => {
    if (!nodeId) { msg.warning("先选择节点"); return; }
    setBusy(true);
    try {
      const r = await lbOp(nodeId, req);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      msg.success(ok);
      await loadGroups(nodeId);
    } catch (e) {
      msg.error(`操作失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const add = async (values: { name: string; host: string; port: number; weight?: number; healthMode: string; healthPath?: string }) => {
    await run({
      op: "add", name: values.name, host: values.host, port: values.port,
      weight: values.weight ?? 1, health_mode: values.healthMode,
      health_path: values.healthPath,
    }, "后端已加入上游组（nginx -t 校验通过后生效）");
  };

  const health = async () => {
    await run({ op: "health" }, "健康检查完成（连续 3 次失败自动判下线）");
  };

  const rows = groups.flatMap((g) =>
    g.backends.map((b) => ({ key: `${g.name}:${b.host}:${b.port}`, group: g.name, ...b })),
  );

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>负载均衡</Typography.Title>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="Nginx upstream 片段管理：变更先经 nginx -t 校验，失败不生效并回滚；健康检查 TCP/HTTP 二选一，间隔 30s，连续 3 次失败判下线；摘除/恢复留审计。" />
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ width: 260 }}
          value={nodeId || undefined}
          placeholder="节点"
          onChange={setNodeId}
          options={nodes.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
        <Button loading={busy} onClick={() => void loadGroups(nodeId)}>刷新</Button>
        <Button loading={busy} onClick={() => void health()}>健康检查</Button>
      </Space>

      <Card size="small" title="上游组与后端">
        <Table
          size="small"
          pagination={false}
          dataSource={rows}
          locale={{ emptyText: "暂无上游组，先在下方添加后端" }}
          columns={[
            { title: "上游组", render: (_: unknown, r: (typeof rows)[number]) => r.group },
            { title: "后端", render: (_: unknown, r: (typeof rows)[number]) => `${r.host}:${r.port}` },
            { title: "权重", dataIndex: "weight" },
            { title: "健康模式", render: (_: unknown, r: (typeof rows)[number]) => r.health_mode.toUpperCase() },
            {
              title: "状态",
              render: (_: unknown, r: (typeof rows)[number]) =>
                r.down ? <Tag color="red">已下线</Tag> : <Tag color="green">健康</Tag>,
            },
            {
              title: "操作",
              render: (_: unknown, r: (typeof rows)[number]) => (
                <Space>
                  {r.down ? (
                    <Button size="small" loading={busy}
                      onClick={() => void run({ op: "undrain", name: r.group, host: r.host, port: r.port }, "后端已恢复")}>
                      恢复
                    </Button>
                  ) : (
                    <Button size="small" danger loading={busy}
                      onClick={() => void run({ op: "drain", name: r.group, host: r.host, port: r.port }, "后端已摘除")}>
                      摘除
                    </Button>
                  )}
                  <Button size="small" danger loading={busy}
                    onClick={() => void run({ op: "remove", name: r.group, host: r.host, port: r.port }, "后端已移除")}>
                    移除
                  </Button>
                </Space>
              ),
            },
          ]}
        />
      </Card>

      <Card size="small" title="添加后端" style={{ marginTop: 12 }}>
        <Form layout="inline" onFinish={add} initialValues={{ weight: 1, healthMode: "tcp" }}>
          <Form.Item name="name" rules={[{ required: true, message: "上游组名必填" }]}>
            <Input placeholder="上游组名（如 web-pool）" style={{ width: 180 }} />
          </Form.Item>
          <Form.Item name="host" rules={[{ required: true, message: "后端地址必填" }]}>
            <Input placeholder="后端地址（如 10.0.0.11）" style={{ width: 200 }} />
          </Form.Item>
          <Form.Item name="port" rules={[{ required: true, message: "端口必填" }]}>
            <InputNumber min={1} max={65535} placeholder="端口" style={{ width: 110 }} />
          </Form.Item>
          <Form.Item name="weight">
            <InputNumber min={1} max={100} placeholder="权重" style={{ width: 100 }} />
          </Form.Item>
          <Form.Item name="healthMode">
            <Select style={{ width: 130 }} options={[{ value: "tcp", label: "TCP 探活" }, { value: "http", label: "HTTP 探活" }]} />
          </Form.Item>
          <Form.Item name="healthPath">
            <Input placeholder="HTTP 路径（如 /healthz）" style={{ width: 180 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>添加</Button>
          </Form.Item>
        </Form>
      </Card>
    </div>
  );
}
