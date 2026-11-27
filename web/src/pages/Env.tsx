import { useCallback, useEffect, useState } from "react";
import { Button, Card, Form, Input, Popconfirm, Select, Space, Table, Tag, message } from "antd";
import { api } from "../api/client";

// 环境变量（运行环境）页（真连 POST /api/nodes/{id}/env，backlog B2 切真）。
// 运行环境探测/离线导入/安装，走 env.manage 通道；detect 只读，import/install 限 Operator+。

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

// Agent 侧 KnownEnvironments（agent/internal/modules/env/env.go）同源清单。
const KNOWN_ENVIRONMENTS = ["nginx", "apache", "mysql", "php", "node", "java", "docker"];

interface EnvRow {
  env: string;
  installed: boolean | null; // null = 未探测
  detecting: boolean;
}

async function envOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/env`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 120000, ...req }),
  });
}

export default function Env() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [rows, setRows] = useState<EnvRow[]>(KNOWN_ENVIRONMENTS.map((env) => ({ env, installed: null, detecting: false })));
  const [form] = Form.useForm();

  useEffect(() => {
    api<NodeView[]>("/api/nodes").then((list) => {
      setNodes(list);
      const first = list.find((n) => n.commandConnected) ?? list[0];
      if (first) setNodeId(first.nodeId);
    }).catch(() => message.error("节点列表加载失败"));
  }, []);

  const detect = useCallback(async (env: string) => {
    if (!nodeId) return;
    setRows((prev) => prev.map((r) => (r.env === env ? { ...r, detecting: true } : r)));
    try {
      const res = await envOp(nodeId, { op: "detect", env });
      // detect 的结果按模块约定落 Output JSON（{"installed": bool}）；旧文本 true/false 兼容。
      let installed: boolean | null = null;
      try {
        const parsed = JSON.parse(res.output);
        installed = typeof parsed?.installed === "boolean" ? parsed.installed : null;
      } catch {
        if (res.output.trim() === "true") installed = true;
        else if (res.output.trim() === "false") installed = false;
      }
      setRows((prev) => prev.map((r) => (r.env === env ? { ...r, installed, detecting: false } : r)));
    } catch {
      message.error(`探测 ${env} 失败`);
      setRows((prev) => prev.map((r) => (r.env === env ? { ...r, detecting: false } : r)));
    }
  }, [nodeId]);

  const detectAll = useCallback(() => {
    KNOWN_ENVIRONMENTS.forEach((env) => void detect(env));
  }, [detect]);

  useEffect(() => { detectAll(); }, [detectAll]);

  const onInstall = async (env: string) => {
    try {
      const res = await envOp(nodeId, { op: "install", env });
      if (res.exit_code !== 0) {
        message.error(`安装 ${env} 失败：${res.output}`);
        return;
      }
      message.success(`${env} 安装完成`);
      void detect(env);
    } catch (e) {
      message.error(`安装失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onImport = async () => {
    const values = await form.validateFields();
    try {
      const res = await envOp(nodeId, { op: "import", env: values.env, src: values.src });
      if (res.exit_code !== 0) {
        message.error(`导入失败：${res.output}`);
        return;
      }
      message.success(`离线包已导入：${res.output}`);
    } catch (e) {
      message.error(`导入失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Card
        title="环境变量"
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
            <Button onClick={detectAll}>重新探测</Button>
          </Space>
        }
      >
        <Table<EnvRow>
          rowKey="env"
          dataSource={rows}
          pagination={false}
          columns={[
            { title: "运行环境", dataIndex: "env" },
            {
              title: "状态",
              width: 140,
              render: (_, row) =>
                row.installed === null ? (
                  <Tag>{row.detecting ? "探测中…" : "未探测"}</Tag>
                ) : row.installed ? (
                  <Tag color="green">已安装</Tag>
                ) : (
                  <Tag color="orange">未安装</Tag>
                ),
            },
            {
              title: "操作",
              width: 220,
              render: (_, row) => (
                <Space>
                  <Button size="small" onClick={() => void detect(row.env)}>探测</Button>
                  <Popconfirm title={`确认在节点上安装 ${row.env}？`} onConfirm={() => onInstall(row.env)}>
                    <Button size="small">安装</Button>
                  </Popconfirm>
                </Space>
              ),
            },
          ]}
        />
      </Card>

      <Card title="离线包导入" style={{ marginTop: 16 }}>
        <Form form={form} layout="inline" style={{ rowGap: 8 }}>
          <Form.Item name="env" label="运行环境" rules={[{ required: true, message: "请选择运行环境" }]}>
            <Select
              style={{ minWidth: 160 }}
              options={KNOWN_ENVIRONMENTS.map((e) => ({ value: e, label: e }))}
              placeholder="选择环境"
            />
          </Form.Item>
          <Form.Item name="src" label="离线包路径" rules={[{ required: true, message: "请输入节点上的离线包路径" }]}>
            <Input placeholder="如 /tmp/offline/nginx.tar.gz" style={{ minWidth: 280 }} />
          </Form.Item>
          <Button type="primary" onClick={onImport}>导入</Button>
        </Form>
      </Card>
    </Space>
  );
}
