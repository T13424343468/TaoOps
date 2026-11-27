import { useCallback, useEffect, useState } from "react";
import { Button, Card, Form, Input, InputNumber, Popconfirm, Select, Space, Table, message } from "antd";
import { api } from "../api/client";

// FTP 页（真连 POST /api/nodes/{id}/ftp，backlog B2 切真）。
// 账号清单/新增/删除/目录授权与回收/连接测试，走 ftp.manage 通道；list/test 只读，adduser/deluser/grant/revoke 限 Operator+。

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

interface FtpUser {
  name: string;
  root_dir: string;
  allowed_dirs: string[];
  created_at: string;
}

async function ftpOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/ftp`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 60000, ...req }),
  });
}

// list 的结构化结果按模块约定落 Output JSON（用户数组，含 name/root_dir/allowed_dirs/created_at）。
function parseUsers(output: string): FtpUser[] {
  try {
    const parsed = JSON.parse(output);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

export default function Ftp() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [users, setUsers] = useState<FtpUser[]>([]);
  const [loading, setLoading] = useState(false);
  const [addOpen, setAddOpen] = useState(false);
  const [form] = Form.useForm();
  const [testForm] = Form.useForm();

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
      const res = await ftpOp(nodeId, { op: "list" });
      setUsers(parseUsers(res.output));
    } catch {
      message.error("FTP 账号清单加载失败");
    } finally {
      setLoading(false);
    }
  }, [nodeId]);

  useEffect(() => { refresh(); }, [refresh]);

  const onAddUser = async () => {
    const values = await form.validateFields();
    try {
      const res = await ftpOp(nodeId, {
        op: "adduser",
        user: values.user,
        password: values.password,
        root: values.root,
      });
      if (res.exit_code !== 0) {
        message.error(`新增账号失败：${res.output}`);
        return;
      }
      message.success("FTP 账号已新增");
      setAddOpen(false);
      refresh();
    } catch (e) {
      message.error(`新增账号失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onDelete = async (name: string) => {
    try {
      const res = await ftpOp(nodeId, { op: "deluser", user: name });
      if (res.exit_code !== 0) {
        message.error(`删除账号失败：${res.output}`);
        return;
      }
      message.success("FTP 账号已删除");
      refresh();
    } catch (e) {
      message.error(`删除账号失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onDirOp = async (op: string, user: string, dir: string) => {
    try {
      const res = await ftpOp(nodeId, { op, user, dir });
      if (res.exit_code !== 0) {
        message.error(`目录${op === "grant" ? "授权" : "回收"}失败：${res.output}`);
        return;
      }
      message.success(`目录已${op === "grant" ? "授权" : "回收"}`);
      refresh();
    } catch (e) {
      message.error(`目录操作失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onTest = async () => {
    const values = await testForm.validateFields();
    try {
      const res = await ftpOp(nodeId, { op: "test", host: values.host, port: values.port });
      if (res.exit_code !== 0) {
        message.error(`连接失败：${res.output}`);
        return;
      }
      message.success("FTP 连接正常");
    } catch (e) {
      message.error(`连接失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Card
        title="FTP 管理"
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
            <Button type="primary" onClick={() => setAddOpen(true)}>新增账号</Button>
            <Button onClick={refresh}>刷新</Button>
          </Space>
        }
      >
        <Table<FtpUser>
          rowKey="name"
          loading={loading}
          dataSource={users}
          pagination={false}
          locale={{ emptyText: "暂无 FTP 账号" }}
          columns={[
            { title: "账号", dataIndex: "name" },
            { title: "根目录", dataIndex: "root_dir", ellipsis: true },
            {
              title: "授权目录",
              ellipsis: true,
              render: (_, row) => (row.allowed_dirs?.length ? row.allowed_dirs.join("、") : "—"),
            },
            { title: "创建时间", dataIndex: "created_at", ellipsis: true },
            {
              title: "操作",
              width: 260,
              render: (_, row) => (
                <Space>
                  <Popconfirm
                    title={`确认授权目录 ${row.root_dir} 给 ${row.name}？`}
                    onConfirm={() => onDirOp("grant", row.name, row.root_dir)}
                  >
                    <Button size="small">授权根目录</Button>
                  </Popconfirm>
                  <Popconfirm
                    title={`确认回收 ${row.name} 的根目录授权？`}
                    onConfirm={() => onDirOp("revoke", row.name, row.root_dir)}
                  >
                    <Button size="small">回收</Button>
                  </Popconfirm>
                  <Popconfirm title={`确认删除 FTP 账号 ${row.name}？`} onConfirm={() => onDelete(row.name)}>
                    <Button size="small" danger>删除</Button>
                  </Popconfirm>
                </Space>
              ),
            },
          ]}
        />
      </Card>

      {addOpen && (
        <Card title="新增 FTP 账号" style={{ marginTop: 16 }}>
          <Form form={form} layout="vertical" style={{ maxWidth: 560 }}>
            <Form.Item name="user" label="账号名" rules={[{ required: true, message: "请输入账号名" }]}>
              <Input placeholder="如 deploy" />
            </Form.Item>
            <Form.Item name="password" label="密码" rules={[{ required: true, message: "请输入密码" }]}>
              <Input.Password placeholder="仅提交一次，不回显" />
            </Form.Item>
            <Form.Item name="root" label="根目录" rules={[{ required: true, message: "请输入根目录" }]}>
              <Input placeholder="如 /srv/ftp/deploy" />
            </Form.Item>
            <Space>
              <Button type="primary" onClick={onAddUser}>确认提交</Button>
              <Button onClick={() => setAddOpen(false)}>取消</Button>
            </Space>
          </Form>
        </Card>
      )}

      <Card title="连接测试" style={{ marginTop: 16 }}>
        <Form form={testForm} layout="inline" style={{ rowGap: 8 }}>
          <Form.Item name="host" label="主机" rules={[{ required: true, message: "请输入主机" }]}>
            <Input placeholder="如 127.0.0.1" />
          </Form.Item>
          <Form.Item name="port" label="端口" rules={[{ required: true, message: "请输入端口" }]}>
            <InputNumber min={1} max={65535} placeholder="21" />
          </Form.Item>
          <Button type="primary" onClick={onTest}>测试连接</Button>
        </Form>
      </Card>
    </Space>
  );
}
