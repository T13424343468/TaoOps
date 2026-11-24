import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, InputNumber, Popconfirm, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A21 SSL 证书管理（真连 POST /api/nodes/{id}/ssl，契约 §14）。
// Let's Encrypt 签发/续期（80 端口预检）、证书上传、自签兜底、清单与到期展示；
// 到期前 30 天自动续期与失败告警由面板每 24h 巡检。
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

interface CertEntry {
  domain: string;
  cert_path: string;
  key_path?: string;
  issuer: string;
  not_after: string;
}

async function sslOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/ssl`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 30000, ...req }),
  });
}

const issuerTag: Record<string, React.ReactNode> = {
  letsencrypt: <Tag color="green">Let's Encrypt</Tag>,
  uploaded: <Tag color="blue">上传</Tag>,
  selfsigned: <Tag color="orange">自签</Tag>,
};

export default function SslPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [certs, setCerts] = useState<CertEntry[]>([]);
  const [busy, setBusy] = useState(false);
  const [precheckOut, setPrecheckOut] = useState<string>("");
  const [msg, contextHolder] = message.useMessage();

  const loadNodes = useCallback(async () => {
    const list = await api<NodeView[]>("/api/nodes");
    setNodes(list);
    setNodeId((cur) => cur || list[0]?.nodeId || "");
  }, []);

  const loadCerts = useCallback(async (id: string) => {
    if (!id) return;
    try {
      const r = await sslOp(id, { op: "list" });
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      try { setCerts(JSON.parse(r.output) as CertEntry[]); }
      catch { setCerts([]); }
    } catch (e) {
      msg.error(`加载证书清单失败：${(e as Error).message}`);
    }
  }, [msg]);

  useEffect(() => { void loadNodes(); }, [loadNodes]);
  useEffect(() => { void loadCerts(nodeId); }, [nodeId, loadCerts]);

  const run = async (req: Record<string, unknown>, ok: string) => {
    if (!nodeId) { msg.warning("先选择节点"); return; }
    setBusy(true);
    try {
      const r = await sslOp(nodeId, req);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      msg.success(ok);
      await loadCerts(nodeId);
      return r;
    } catch (e) {
      msg.error(`操作失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const precheck = async (values: { domain: string }) => {
    const r = await run({ op: "precheck", domain: values.domain }, "80 端口可达");
    if (r) setPrecheckOut(r.output);
  };

  const issue = async (values: { domain: string; email: string; webroot: string }) => {
    await run({ op: "issue", domain: values.domain, email: values.email, webroot: values.webroot }, "签发完成");
  };

  const upload = async (values: { domain: string; certPem: string; keyPem: string }) => {
    await run({ op: "upload", domain: values.domain, cert_pem: values.certPem, key_pem: values.keyPem }, "证书已上传");
  };

  const selfsign = async (values: { domain: string; days?: number }) => {
    await run({ op: "selfsign", domain: values.domain, days: values.days }, "自签证书已生成");
  };

  const renew = async (domain: string) => {
    await run({ op: "renew", domain }, `已续期 ${domain}`);
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>SSL 证书</Typography.Title>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="Let's Encrypt 证书签发与自动续期（HTTP-01，要求 80 端口公网可达）；到期前 30 天面板每日自动尝试续期，失败产生告警；离线场景可用自签证书兜底。" />
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ width: 260 }}
          value={nodeId || undefined}
          placeholder="节点"
          onChange={setNodeId}
          options={nodes.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
        <Button loading={busy} onClick={() => void loadCerts(nodeId)}>刷新清单</Button>
      </Space>

      <Card size="small" title="证书清单">
        <Table
          size="small"
          rowKey="domain"
          dataSource={certs}
          pagination={false}
          columns={[
            { title: "域名", dataIndex: "domain" },
            { title: "签发方", dataIndex: "issuer", render: (v: string) => issuerTag[v] ?? v },
            { title: "到期时间", dataIndex: "not_after" },
            { title: "证书路径", dataIndex: "cert_path", ellipsis: true },
            {
              title: "操作",
              render: (_: unknown, rec: CertEntry) => (
                <Popconfirm title={`对 ${rec.domain} 执行续期，确认？`} onConfirm={() => void renew(rec.domain)}>
                  <Button size="small" loading={busy}>续期</Button>
                </Popconfirm>
              ),
            },
          ]}
        />
      </Card>

      <Card size="small" title="80 端口预检" style={{ marginTop: 12 }}>
        <Form layout="inline" onFinish={precheck}>
          <Form.Item name="domain" rules={[{ required: true, message: "域名必填" }]}>
            <Input placeholder="域名（如 example.com）" style={{ width: 240 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>预检</Button>
          </Form.Item>
        </Form>
        {precheckOut && <Typography.Text type="secondary" style={{ marginTop: 8, display: "block" }}>{precheckOut}</Typography.Text>}
      </Card>

      <Card size="small" title="签发 Let's Encrypt 证书" style={{ marginTop: 12 }}>
        <Form layout="inline" onFinish={issue}>
          <Form.Item name="domain" rules={[{ required: true, message: "域名必填" }]}>
            <Input placeholder="域名" style={{ width: 180 }} />
          </Form.Item>
          <Form.Item name="email" rules={[{ required: true, message: "邮箱必填" }]}>
            <Input placeholder="邮箱" style={{ width: 200 }} />
          </Form.Item>
          <Form.Item name="webroot" rules={[{ required: true, message: "站点根目录必填" }]}>
            <Input placeholder="站点根目录（如 /var/www）" style={{ width: 220 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>签发</Button>
          </Form.Item>
        </Form>
      </Card>

      <Card size="small" title="上传已有证书" style={{ marginTop: 12 }}>
        <Form layout="vertical" onFinish={upload}>
          <Form.Item name="domain" rules={[{ required: true, message: "域名必填" }]}>
            <Input placeholder="域名" style={{ width: 240 }} />
          </Form.Item>
          <Form.Item name="certPem" rules={[{ required: true, message: "证书 PEM 必填" }]}>
            <Input.TextArea rows={3} placeholder="证书 PEM（fullchain）" />
          </Form.Item>
          <Form.Item name="keyPem" rules={[{ required: true, message: "私钥 PEM 必填" }]}>
            <Input.TextArea rows={3} placeholder="私钥 PEM（落盘权限 600）" />
          </Form.Item>
          <Button type="primary" htmlType="submit" loading={busy}>上传</Button>
        </Form>
      </Card>

      <Card size="small" title="自签证书（离线兜底）" style={{ marginTop: 12 }}>
        <Form layout="inline" onFinish={selfsign} initialValues={{ days: 3650 }}>
          <Form.Item name="domain" rules={[{ required: true, message: "域名必填" }]}>
            <Input placeholder="域名" style={{ width: 200 }} />
          </Form.Item>
          <Form.Item name="days">
            <InputNumber min={1} placeholder="有效天数" style={{ width: 120 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>生成</Button>
          </Form.Item>
        </Form>
      </Card>
    </div>
  );
}
