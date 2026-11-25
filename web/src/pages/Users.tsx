import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, Modal, Popconfirm, Select, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// backlog F1 用户管理页：真连 /api/auth/users（Admin only，后端 AdminOnlyPrefixes 已含）。
// 列表/创建/删除；角色 Admin/Operator/ReadOnly。创建密码仅提交一次，不回显不落审计。

interface UserRow {
  username: string;
  role: string;
  createdAtMs: number;
}

const roleTag: Record<string, React.ReactNode> = {
  Admin: <Tag color="red">Admin</Tag>,
  Operator: <Tag color="blue">Operator</Tag>,
  ReadOnly: <Tag color="green">ReadOnly</Tag>,
};

export default function UsersPage() {
  const [users, setUsers] = useState<UserRow[]>([]);
  const [addOpen, setAddOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [form] = Form.useForm();
  const [msg, contextHolder] = message.useMessage();

  const load = useCallback(async () => {
    try { setUsers(await api<UserRow[]>("/api/auth/users")); }
    catch (e) { msg.error(`用户列表加载失败：${(e as Error).message}`); }
  }, [msg]);

  useEffect(() => { void load(); }, [load]);

  const addUser = async (values: { username: string; password: string; role: string }) => {
    setBusy(true);
    try {
      await api("/api/auth/users", {
        method: "POST",
        body: JSON.stringify(values),
      });
      msg.success(`用户 ${values.username} 已创建`);
      setAddOpen(false);
      form.resetFields();
      await load();
    } catch (e) {
      msg.error(`创建失败：${(e as Error).message}`);
    } finally { setBusy(false); }
  };

  const removeUser = async (username: string) => {
    try {
      await api(`/api/auth/users/${encodeURIComponent(username)}`, { method: "DELETE" });
      msg.success(`用户 ${username} 已删除`);
      await load();
    } catch (e) {
      msg.error(`删除失败：${(e as Error).message}`);
    }
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>用户管理</Typography.Title>
      <Card
        size="small"
        title="面板账号"
        extra={<Button type="primary" onClick={() => setAddOpen(true)}>新增用户</Button>}
      >
        <Table
          rowKey="username"
          size="small"
          pagination={false}
          dataSource={users}
          columns={[
            { title: "用户名", dataIndex: "username" },
            { title: "角色", dataIndex: "role", render: (r: string) => roleTag[r] ?? r },
            { title: "创建时间", dataIndex: "createdAtMs", render: (ms: number) => (ms > 0 ? new Date(ms).toLocaleString() : "—") },
            {
              title: "操作",
              render: (_, u: UserRow) => (
                <Popconfirm title={`删除用户 ${u.username}？`} okText="删除" okType="danger" onConfirm={() => removeUser(u.username)}>
                  <Button size="small" danger>删除</Button>
                </Popconfirm>
              ),
            },
          ]}
        />
        <Alert style={{ marginTop: 12 }} type="info" showIcon
          message="仅 Admin 可见本页"
          description="创建时密码仅提交一次（不回显不落审计）；删除操作不可恢复。" />
      </Card>

      <Modal
        title="新增用户"
        open={addOpen}
        onCancel={() => setAddOpen(false)}
        onOk={() => form.submit()}
        confirmLoading={busy}
        okText="创建"
      >
        <Form form={form} layout="vertical" initialValues={{ role: "ReadOnly" }} onFinish={addUser}>
          <Form.Item name="username" label="用户名" rules={[
            { required: true, message: "请输入用户名" },
            { pattern: /^[a-zA-Z0-9_-]{2,32}$/, message: "2-32 位字母数字下划线短横线" },
          ]}>
            <Input placeholder="ops-user" />
          </Form.Item>
          <Form.Item name="password" label="初始密码" rules={[
            { required: true, message: "请输入初始密码" },
            { min: 8, message: "至少 8 位" },
          ]}>
            <Input.Password placeholder="至少 8 位，登录后建议尽快修改" />
          </Form.Item>
          <Form.Item name="role" label="角色" rules={[{ required: true }]}>
            <Select options={[
              { value: "Admin", label: "Admin（全部权限）" },
              { value: "Operator", label: "Operator（读写）" },
              { value: "ReadOnly", label: "ReadOnly（只读）" },
            ]} />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  );
}
