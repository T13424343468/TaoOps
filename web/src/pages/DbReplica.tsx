import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Descriptions, Form, Input, InputNumber, Popconfirm, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A15 MySQL 主从复制（真连 POST /api/nodes/{id}/db，契约 §13）。
// 主库授权（密码仅展示一次）、从库配置（变更前自动备份 my.cnf）、
// 复制状态查询、断链重置；复制中断与延迟超阈值由面板每 60s 巡检告警。
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

interface SlaveStatus {
  configured: boolean;
  io_running: string;
  sql_running: string;
  seconds_behind_master: number;
  master_host: string;
  last_error: string;
}

async function dbOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/db`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 30000, ...req }),
  });
}

export default function DbReplicaPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [status, setStatus] = useState<SlaveStatus | null>(null);
  const [grantOut, setGrantOut] = useState<string>("");
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();

  const loadNodes = useCallback(async () => {
    const list = await api<NodeView[]>("/api/nodes");
    setNodes(list);
    setNodeId((cur) => cur || list[0]?.nodeId || "");
  }, []);

  const loadStatus = useCallback(async (id: string) => {
    if (!id) return;
    try {
      const r = await dbOp(id, { op: "replica_status" });
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      try { setStatus(JSON.parse(r.output) as SlaveStatus); }
      catch { setStatus(null); }
    } catch (e) {
      msg.error(`加载复制状态失败：${(e as Error).message}`);
    }
  }, [msg]);

  useEffect(() => { void loadNodes(); }, [loadNodes]);
  useEffect(() => { void loadStatus(nodeId); }, [nodeId, loadStatus]);

  const run = async (req: Record<string, unknown>, ok: string) => {
    if (!nodeId) { msg.warning("先选择节点"); return; }
    setBusy(true);
    try {
      const r = await dbOp(nodeId, req);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      msg.success(ok);
      await loadStatus(nodeId);
      return r;
    } catch (e) {
      msg.error(`操作失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const grant = async (values: { replicaHost: string }) => {
    const r = await run({ op: "master_grant", replica_host: values.replicaHost }, "复制账号已创建");
    if (r) setGrantOut(r.output);
  };

  const config = async (values: { masterHost: string; masterPort?: number; masterUser: string; masterPassword: string; masterLogFile?: string; masterLogPos?: number }) => {
    await run({
      op: "replica_config", master_host: values.masterHost, master_port: String(values.masterPort ?? 3306),
      master_user: values.masterUser, master_password: values.masterPassword,
      master_log_file: values.masterLogFile, master_log_pos: values.masterLogPos,
    }, "从库已配置（变更前已自动备份 my.cnf）");
  };

  const threadTag = (v: string) => (v === "Yes" ? <Tag color="green">正常</Tag> : <Tag color="red">中断</Tag>);

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>主从复制</Typography.Title>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="MySQL 主从复制管理：复制账号密码仅创建时展示一次；从库配置变更前自动备份 my.cnf；复制中断与延迟超阈值（默认 300 秒）由面板每 60 秒巡检并告警。" />
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ width: 260 }}
          value={nodeId || undefined}
          placeholder="节点"
          onChange={setNodeId}
          options={nodes.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
        <Button loading={busy} onClick={() => void loadStatus(nodeId)}>刷新状态</Button>
      </Space>

      <Card size="small" title="复制状态">
        {status?.configured ? (
          <Descriptions size="small" column={2}>
            <Descriptions.Item label="IO 线程">{threadTag(status.io_running)}</Descriptions.Item>
            <Descriptions.Item label="SQL 线程">{threadTag(status.sql_running)}</Descriptions.Item>
            <Descriptions.Item label="延迟（秒）">{status.seconds_behind_master}</Descriptions.Item>
            <Descriptions.Item label="主库">{status.master_host || "—"}</Descriptions.Item>
            {status.last_error && <Descriptions.Item label="最近错误" span={2}>{status.last_error}</Descriptions.Item>}
          </Descriptions>
        ) : (
          <Alert type="warning" showIcon message="该节点未配置复制（或状态不可用）" />
        )}
        {status?.configured && (
          <Popconfirm title="断链重置将清除该节点的复制配置，确认？" onConfirm={() => void run({ op: "replica_reset" }, "复制已重置")}>
            <Button danger size="small" style={{ marginTop: 12 }} loading={busy}>断链重置</Button>
          </Popconfirm>
        )}
      </Card>

      <Card size="small" title="主库：创建复制账号" style={{ marginTop: 12 }}>
        <Form layout="inline" onFinish={grant}>
          <Form.Item name="replicaHost" rules={[{ required: true, message: "从库地址必填" }]}>
            <Input placeholder="从库地址（如 10.0.0.22）" style={{ width: 220 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>创建账号</Button>
          </Form.Item>
        </Form>
        {grantOut && (
          <Alert type="success" showIcon style={{ marginTop: 12 }}
            message="凭据仅此一次展示，请立即保存"
            description={<pre style={{ margin: 0, whiteSpace: "pre-wrap" }}>{grantOut}</pre>} />
        )}
      </Card>

      <Card size="small" title="从库：配置复制" style={{ marginTop: 12 }}>
        <Form layout="inline" onFinish={config} initialValues={{ masterPort: 3306 }}>
          <Form.Item name="masterHost" rules={[{ required: true, message: "主库地址必填" }]}>
            <Input placeholder="主库地址" style={{ width: 160 }} />
          </Form.Item>
          <Form.Item name="masterPort">
            <InputNumber min={1} max={65535} placeholder="端口" style={{ width: 100 }} />
          </Form.Item>
          <Form.Item name="masterUser" rules={[{ required: true, message: "复制账号必填" }]}>
            <Input placeholder="复制账号" style={{ width: 140 }} />
          </Form.Item>
          <Form.Item name="masterPassword" rules={[{ required: true, message: "复制密码必填" }]}>
            <Input.Password placeholder="复制密码" style={{ width: 160 }} />
          </Form.Item>
          <Form.Item name="masterLogFile">
            <Input placeholder="日志文件（可选）" style={{ width: 150 }} />
          </Form.Item>
          <Form.Item name="masterLogPos">
            <InputNumber min={0} placeholder="位点（可选）" style={{ width: 120 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>配置</Button>
          </Form.Item>
        </Form>
      </Card>
    </div>
  );
}
