import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, Modal, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A24 应用一键部署：模板库（真连 GET 经 /api/nodes/{id}/apps
// op=templates）、已部署应用列表（op=list）、部署/升级/删除
// （Admin 专用，AppDeployPolicy 分级鉴权）。

interface NodeView {
  nodeId: string;
  hostname: string;
  commandConnected: boolean;
}

interface DeployService {
  name: string;
  image: string;
  ports?: string[];
  health_cmd?: string;
}

interface TemplateView {
  id: string;
  name: string;
  arch: string;
  kind: string;
  services: DeployService[];
}

interface AppStateView {
  app: string;
  template_id: string;
  images: string[];
  deployed_at: string;
  health: string;
}

interface CmdOutcome {
  exit_code: number;
  output: string;
  request_id: string;
}

const HEALTH_COLOR: Record<string, string> = {
  healthy: "green",
  unhealthy: "red",
  unknown: "orange",
};

export default function AppDeployPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [templates, setTemplates] = useState<TemplateView[]>([]);
  const [apps, setApps] = useState<AppStateView[]>([]);
  const [deployOpen, setDeployOpen] = useState(false);
  const [deployTpl, setDeployTpl] = useState<TemplateView | null>(null);
  const [busy, setBusy] = useState(false);
  const [form] = Form.useForm();

  const loadNodes = useCallback(async () => {
    try {
      const list = await api<NodeView[]>("/api/nodes");
      setNodes(Array.isArray(list) ? list : []);
      setNodeId((cur) => cur || list[0]?.nodeId || "");
    } catch { setNodes([]); }
  }, []);

  const loadAll = useCallback(async (id: string) => {
    if (!id) return;
    const post = async (op: string) => {
      try {
        const r = await api<CmdOutcome>(`/api/nodes/${encodeURIComponent(id)}/apps`, {
          method: "POST",
          body: JSON.stringify({ op, timeout_ms: 30000 }),
        });
        if (r.exit_code !== 0) return null;
        try { return JSON.parse(r.output); } catch { return null; }
      } catch { return null; }
    };
    const t = await post("templates");
    setTemplates(t?.templates && Array.isArray(t.templates) ? t.templates : []);
    const l = await post("list");
    setApps(l?.apps && Array.isArray(l.apps) ? l.apps : []);
  }, []);

  useEffect(() => { loadNodes(); }, [loadNodes]);
  useEffect(() => { loadAll(nodeId); }, [nodeId, loadAll]);

  const runOp = async (op: string, app: string, extra?: Record<string, unknown>, okMsg?: string) => {
    if (!nodeId) return;
    setBusy(true);
    try {
      const r = await api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/apps`, {
        method: "POST",
        body: JSON.stringify({ op, app, ...extra, timeout_ms: 300000 }),
      });
      if (r.exit_code === 0) { message.success(okMsg || "操作成功"); await loadAll(nodeId); }
      else message.error(`操作失败：${r.output || "未知错误"}`);
    } catch (e) { message.error(`操作失败：${e instanceof Error ? e.message : "网络错误"}`); }
    finally { setBusy(false); }
  };

  const onDeploy = async () => {
    const v = await form.validateFields();
    const env: Record<string, string> = {};
    if (v.envText) {
      for (const line of v.envText.split("\n")) {
        const i = line.indexOf("=");
        if (i > 0) env[line.slice(0, i).trim()] = line.slice(i + 1).trim();
      }
    }
    await runOp("deploy", v.app, { template: deployTpl?.id, env }, "部署已提交，健康检查通过后生效");
    setDeployOpen(false);
  };

  const nodeOnline = nodes.find((n) => n.nodeId === nodeId)?.commandConnected;

  return (
    <Space direction="vertical" size="middle" style={{ width: "100%" }}>
      <Card
        title="应用一键部署"
        extra={
          <Space>
            <Select
              style={{ width: 220 }}
              value={nodeId || undefined}
              onChange={setNodeId}
              options={nodes.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId})` }))}
              placeholder="选择节点"
            />
            <Button onClick={() => nodeId && loadAll(nodeId)}>刷新</Button>
          </Space>
        }
      >
        {!nodeOnline && <Alert type="warning" showIcon message="节点命令通道未连接，部署操作不可用" style={{ marginBottom: 12 }} />}
        <Table<TemplateView>
          rowKey="id"
          size="small"
          pagination={false}
          dataSource={templates}
          columns={[
            { title: "模板", dataIndex: "name", key: "name" },
            { title: "类型", key: "kind", render: (_, t) => (t.kind === "stack" ? <Tag>多服务</Tag> : <Tag>单体</Tag>) },
            { title: "架构", dataIndex: "arch", key: "arch" },
            { title: "镜像", key: "images", render: (_, t) => t.services.map((s) => s.image).join(" + ") },
            {
              title: "操作",
              key: "act",
              render: (_, t) => (
                <Button
                  type="primary"
                  size="small"
                  disabled={!nodeOnline || busy}
                  onClick={() => { setDeployTpl(t); form.resetFields(); setDeployOpen(true); }}
                >
                  一键部署
                </Button>
              ),
            },
          ]}
        />
      </Card>

      <Card title="已部署应用">
        <Table<AppStateView>
          rowKey="app"
          size="small"
          pagination={false}
          dataSource={apps}
          columns={[
            { title: "应用名", dataIndex: "app", key: "app" },
            { title: "模板", dataIndex: "template_id", key: "tpl" },
            { title: "健康", dataIndex: "health", key: "health", render: (h: string) => <Tag color={HEALTH_COLOR[h] || "orange"}>{h}</Tag> },
            { title: "部署时间", dataIndex: "deployed_at", key: "at" },
            {
              title: "操作",
              key: "act",
              render: (_, a) => (
                <Space>
                  <Button size="small" disabled={!nodeOnline || busy} onClick={() => runOp("upgrade", a.app, {}, "升级已提交，失败自动回滚")}>升级</Button>
                  <Button
                    size="small"
                    danger
                    disabled={!nodeOnline || busy}
                    onClick={() => Modal.confirm({
                      title: `删除应用 ${a.app}？`,
                      content: "将停止并删除该应用的全部容器与数据目录记录，不可恢复。",
                      onOk: () => runOp("remove", a.app, {}, "已删除"),
                    })}
                  >
                    删除
                  </Button>
                </Space>
              ),
            },
          ]}
        />
      </Card>

      <Modal
        title={`一键部署：${deployTpl?.name || ""}`}
        open={deployOpen}
        onOk={onDeploy}
        confirmLoading={busy}
        onCancel={() => setDeployOpen(false)}
        okText="部署"
        cancelText="取消"
      >
        <Typography.Paragraph type="secondary">
          将在所选节点以 Compose 拉起：{deployTpl?.services.map((s) => s.image).join(" + ")}。
          部署后自动健康检查，失败自动清理。
        </Typography.Paragraph>
        <Form form={form} layout="vertical">
          <Form.Item name="app" label="应用名" rules={[{ required: true, pattern: /^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$/, message: "字母数字开头，可含 . _ -" }]}>
            <Input placeholder="例如 my-site" />
          </Form.Item>
          <Form.Item name="envText" label="环境变量覆盖（每行 KEY=value，可选）">
            <Input.TextArea rows={3} placeholder={"MYSQL_ROOT_PASSWORD=strong-pass\nWORDPRESS_DB_PASSWORD=strong-pass"} />
          </Form.Item>
        </Form>
      </Modal>
    </Space>
  );
}
