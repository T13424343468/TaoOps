import { useCallback, useEffect, useState } from "react";
import { Button, Card, Drawer, Form, Input, InputNumber, Popconfirm, Select, Space, Table, Typography, message } from "antd";
import { api } from "../api/client";

// 网站页（真连 POST /api/nodes/{id}/site，backlog B2 切真）。
// 站点列表/新增/编辑/删除/备份列表，走 site.manage 通道；list/backups 只读，create/update/delete 限 Operator+。

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

interface SiteRow {
  name: string;
  config: string;
  server_name: string;
  proxy_pass: string;
}

async function siteOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/site`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 60000, ...req }),
  });
}

// list 的结构化结果按模块约定落 Output JSON（sites 数组）。
function parseSites(output: string): SiteRow[] {
  try {
    const parsed = JSON.parse(output);
    return Array.isArray(parsed?.sites) ? parsed.sites : [];
  } catch {
    return [];
  }
}

export default function Site() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [sites, setSites] = useState<SiteRow[]>([]);
  const [loading, setLoading] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  const [editingName, setEditingName] = useState<string>(""); // 空 = 新增
  const [form] = Form.useForm();
  const [cfgTarget, setCfgTarget] = useState<SiteRow | null>(null);
  const [backups, setBackups] = useState<string[]>([]);

  useEffect(() => {
    api<NodeView[]>("/api/nodes").then((list) => {
      setNodes(list);
      const first = list.find((n) => n.commandConnected) ?? list[0];
      if (first) setNodeId(first.nodeId);
    }).catch(() => message.error("节点列表加载失败"));
  }, []);

  const refresh = useCallback(async () => {
    if (!nodeId) return;
    setLoading(true);
    try {
      const res = await siteOp(nodeId, { op: "list" });
      setSites(parseSites(res.output));
    } catch {
      message.error("站点列表加载失败");
    } finally {
      setLoading(false);
    }
  }, [nodeId]);

  useEffect(() => { refresh(); }, [refresh]);

  const openCreate = () => {
    setEditingName("");
    form.setFieldsValue({ name: "", domains: "", proxy: "", port: 80 });
    setEditOpen(true);
  };

  const openEdit = (row: SiteRow) => {
    setEditingName(row.name);
    form.setFieldsValue({
      name: row.name,
      domains: row.server_name,
      proxy: row.proxy_pass,
      port: undefined,
    });
    setEditOpen(true);
  };

  const onSubmit = async () => {
    const values = await form.validateFields();
    const op = editingName === "" ? "create" : "update";
    try {
      await siteOp(nodeId, {
        op,
        name: values.name,
        domains: values.domains ?? "",
        proxy: values.proxy ?? "",
        port: values.port ?? 0,
      });
      message.success(op === "create" ? "站点已创建" : "站点已更新");
      setEditOpen(false);
      refresh();
    } catch (e) {
      message.error(`提交失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onDelete = async (name: string) => {
    try {
      await siteOp(nodeId, { op: "delete", name });
      message.success("站点已删除");
      refresh();
    } catch (e) {
      message.error(`删除失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onBackups = async (name: string) => {
    setCfgTarget(null);
    try {
      const res = await siteOp(nodeId, { op: "backups", name });
      const parsed = JSON.parse(res.output);
      setBackups(Array.isArray(parsed?.backups) ? parsed.backups : []);
      setCfgTarget({ name, config: "", server_name: "", proxy_pass: "" });
    } catch {
      message.error("备份列表加载失败");
    }
  };

  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Card
        title="网站"
        extra={
          <Space>
            <Select
              style={{ minWidth: 220 }}
              value={nodeId || undefined}
              onChange={setNodeId}
              options={nodes.map((n) => ({
                value: n.nodeId,
                label: `${n.hostname}${n.commandConnected ? "" : "（离线）"}`,
              }))}
              placeholder="选择节点"
            />
            <Button type="primary" onClick={openCreate}>新增站点</Button>
            <Button onClick={refresh}>刷新</Button>
          </Space>
        }
      >
        <Table<SiteRow>
          rowKey="name"
          loading={loading}
          dataSource={sites}
          pagination={false}
          columns={[
            { title: "站点名", dataIndex: "name" },
            { title: "域名", dataIndex: "server_name", ellipsis: true },
            { title: "反代目标", dataIndex: "proxy_pass", ellipsis: true },
            {
              title: "操作",
              render: (_, row) => (
                <Space>
                  <Button size="small" onClick={() => setCfgTarget(row)}>配置</Button>
                  <Button size="small" onClick={() => onBackups(row.name)}>备份</Button>
                  <Button size="small" onClick={() => openEdit(row)}>编辑</Button>
                  <Popconfirm title={`确认删除站点 ${row.name}？`} onConfirm={() => onDelete(row.name)}>
                    <Button size="small" danger>删除</Button>
                  </Popconfirm>
                </Space>
              ),
            },
          ]}
        />
      </Card>

      <Drawer
        title={cfgTarget?.name ? `站点详情 · ${cfgTarget.name}` : ""}
        open={cfgTarget !== null}
        onClose={() => setCfgTarget(null)}
        width={560}
      >
        {cfgTarget?.config ? (
          <pre style={{ whiteSpace: "pre-wrap", fontSize: 12 }}>{cfgTarget.config}</pre>
        ) : (
          backups.length === 0 ? (
            <Typography.Text type="secondary">暂无备份文件</Typography.Text>
          ) : (
            <ul style={{ paddingLeft: 20 }}>
              {backups.map((b) => <li key={b} style={{ fontSize: 12, wordBreak: "break-all" }}>{b}</li>)}
            </ul>
          )
        )}
      </Drawer>

      {editOpen && (
        <Card title={editingName === "" ? "新增站点" : `编辑站点 · ${editingName}`} style={{ marginTop: 16 }}>
          <Form form={form} layout="vertical" style={{ maxWidth: 560 }}>
            <Form.Item name="name" label="站点名" rules={[{ required: true, message: "请输入站点名" }]}>
              <Input placeholder="如 my-site" disabled={editingName !== ""} />
            </Form.Item>
            <Form.Item name="domains" label="域名（server_name）">
              <Input placeholder="如 example.com www.example.com" />
            </Form.Item>
            <Form.Item name="proxy" label="反代目标（proxy_pass）">
              <Input placeholder="如 http://127.0.0.1:3000" />
            </Form.Item>
            <Form.Item name="port" label="监听端口">
              <InputNumber min={1} max={65535} style={{ width: "100%" }} />
            </Form.Item>
            <Space>
              <Button type="primary" onClick={onSubmit}>确认提交</Button>
              <Button onClick={() => setEditOpen(false)}>取消</Button>
            </Space>
          </Form>
        </Card>
      )}
    </Space>
  );
}
