import { useCallback, useEffect, useState } from "react";
import { Button, Card, Form, Input, Popconfirm, Select, Space, Table, Typography, message } from "antd";
import { api } from "../api/client";

// 数据库页（真连 POST /api/nodes/{id}/db/ops，backlog B2 切真）。
// 库备份清单/建库/删库/备份/还原，走 db.manage 通道；backups 只读，create/drop/backup/restore 限 Operator+。

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

interface BackupRow {
  file: string;
}

async function dbOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/db/ops`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 120000, ...req }),
  });
}

// backups 的结构化结果按模块约定落 Output JSON（字符串数组）。
function parseBackups(output: string): BackupRow[] {
  try {
    const parsed = JSON.parse(output);
    return Array.isArray(parsed) ? parsed.map((f) => ({ file: String(f) })) : [];
  } catch {
    return [];
  }
}

export default function Db() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [engine, setEngine] = useState<string>("mysql");
  const [backups, setBackups] = useState<BackupRow[]>([]);
  const [loading, setLoading] = useState(false);
  const [form] = Form.useForm();

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
      const res = await dbOp(nodeId, { op: "backups", engine });
      setBackups(parseBackups(res.output));
    } catch {
      message.error("备份清单加载失败");
    } finally {
      setLoading(false);
    }
  }, [nodeId, engine]);

  useEffect(() => { refresh(); }, [refresh]);

  const runOp = async (op: string, extra: Record<string, unknown>, okText: string) => {
    try {
      const res = await dbOp(nodeId, { op, engine, ...extra });
      if (res.exit_code !== 0) {
        message.error(`${okText}失败：${res.output}`);
        return;
      }
      message.success(okText + "完成");
      refresh();
    } catch (e) {
      message.error(`${okText}失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onCreate = async () => {
    const values = await form.validateFields();
    await runOp("create", { name: values.name }, "建库");
  };

  const onBackup = async (name: string) => {
    await runOp("backup", { name }, "备份");
  };

  const onRestore = async (file: string) => {
    const values = form.getFieldValue("restoreName") as string | undefined;
    if (!values) { message.error("请先在下方填写还原目标库名"); return; }
    await runOp("restore", { name: values, dump: file }, "还原");
  };

  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Card
        title="数据库"
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
            <Select
              style={{ minWidth: 140 }}
              value={engine}
              onChange={setEngine}
              options={[
                { value: "mysql", label: "MySQL" },
                { value: "postgres", label: "PostgreSQL" },
              ]}
            />
            <Button onClick={refresh}>刷新</Button>
          </Space>
        }
      >
        <Form form={form} layout="inline" style={{ marginBottom: 16, rowGap: 8 }}>
          <Form.Item name="name" label="库名" rules={[{ required: true, message: "请输入库名" }]}>
            <Input placeholder="如 app_prod" />
          </Form.Item>
          <Button type="primary" onClick={onCreate}>建库</Button>
          <Popconfirm title="确认对该库立即备份？">
            <Button onClick={async () => {
              const name = form.getFieldValue("name") as string | undefined;
              if (!name) { message.error("请先填写库名"); return; }
              await onBackup(name);
            }}>备份</Button>
          </Popconfirm>
          <Popconfirm title="确认删除该库？库内数据将全部丢失！" onConfirm={async () => {
            const name = form.getFieldValue("name") as string | undefined;
            if (!name) { message.error("请先填写库名"); return; }
            await runOp("drop", { name }, "删库");
          }}>
            <Button danger>删库</Button>
          </Popconfirm>
        </Form>
        <Table<BackupRow>
          rowKey="file"
          loading={loading}
          dataSource={backups}
          pagination={false}
          locale={{ emptyText: "暂无备份文件" }}
          columns={[
            { title: "备份文件", dataIndex: "file", ellipsis: true },
            {
              title: "操作",
              width: 120,
              render: (_, row) => (
                <Popconfirm title={`确认用 ${row.file} 还原？目标库同名表将被覆盖。`} onConfirm={() => onRestore(row.file)}>
                  <Button size="small">还原</Button>
                </Popconfirm>
              ),
            },
          ]}
        />
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          提示：还原前请在上方"库名"输入框填写目标库名；对已有库可点"备份"生成快照。
        </Typography.Text>
      </Card>
    </Space>
  );
}
