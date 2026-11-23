import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Switch, Table, Typography, message } from "antd";
import { api } from "../api/client";

export interface WafRule {
  id: string;
  name: string;
  category: "Sqli" | "Xss" | "Scanner" | "CcRate" | "IpBlacklist" | "IpWhitelist" | "Custom";
  pattern: string;
  priority: number;
  enabled: boolean;
  action?: string;
  windowSeconds?: number;
  maxRequests?: number;
}

const CATEGORIES = [
  { value: "Sqli", label: "SQL 注入" },
  { value: "Xss", label: "XSS" },
  { value: "Scanner", label: "扫描器" },
  { value: "CcRate", label: "CC 频率" },
  { value: "IpBlacklist", label: "IP 黑名单" },
  { value: "IpWhitelist", label: "IP 白名单" },
  { value: "Custom", label: "自定义正则" },
];

export default function WafPage() {
  const [rules, setRules] = useState<WafRule[]>([]);
  const [events, setEvents] = useState<unknown[]>([]);
  const [editing, setEditing] = useState<WafRule | null>(null);
  const [creating, setCreating] = useState(false);
  const [form] = Form.useForm();
  const [msg, contextHolder] = message.useMessage();

  const load = useCallback(async () => {
    setRules(await api<WafRule[]>("/api/waf/rules"));
    setEvents(await api<unknown[]>("/api/waf/events"));
  }, []);

  useEffect(() => { load().catch((e) => msg.error(`加载失败: ${e.message}`)); }, [load, msg]);

  const save = async () => {
    const v = form.getFieldsValue();
    try {
      if (creating) await api("/api/waf/rules", { method: "POST", body: JSON.stringify(v) });
      else await api(`/api/waf/rules/${editing!.id}`, { method: "PUT", body: JSON.stringify({ ...v, id: editing!.id }) });
      setEditing(null); setCreating(false);
      await load();
      msg.success("已保存");
    } catch (e) { msg.error(`保存失败: ${e instanceof Error ? e.message : e}`); }
  };

  const toggle = async (r: WafRule, enabled: boolean) => {
    await api(`/api/waf/rules/${r.id}/enabled?enabled=${enabled}`, { method: "PATCH" });
    await load();
  };


  const remove = async (id: string) => {
    await api(`/api/waf/rules/${id}`, { method: "DELETE" });
    await load();
  };

  const columns = [
    { title: "启用", dataIndex: "enabled", render: (_: boolean, r: WafRule) => <Switch size="small" checked={r.enabled} onChange={(v: boolean) => toggle(r, v)} /> },
    { title: "名称", dataIndex: "name" },
    { title: "类目", dataIndex: "category", render: (c: string) => CATEGORIES.find((x) => x.value === c)?.label ?? c },
    { title: "优先级", dataIndex: "priority" },
    { title: "匹配", dataIndex: "pattern", ellipsis: true, render: (p: string) => <code>{p}</code> },
    {
      title: "操作",
      render: (_: unknown, r: WafRule) => (
        <Space>
          <Button size="small" onClick={() => { setEditing(r); setCreating(false); form.setFieldsValue(r); }}>编辑</Button>
          <Popconfirm title="确认删除？" onConfirm={() => remove(r.id).catch((e) => msg.error(e.message))}>
            <Button size="small" danger>删除</Button>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>WAF 防火墙</Typography.Title>
      <Space style={{ marginBottom: 12 }}>
        <Button type="primary" onClick={() => { setCreating(true); setEditing(null); form.resetFields(); }}>新增规则</Button>
        <Button onClick={() => window.open("/api/waf/config/preview")}>预览生成配置</Button>
        <Button onClick={() => window.open("/api/waf/rules/export")}>导出规则</Button>
      </Space>
      <Table rowKey="id" size="small" pagination={false} dataSource={rules} columns={columns} />
      <Card title="拦截事件（最近）" size="small" style={{ marginTop: 16 }}>
        {events.length === 0 ? <Alert type="info" message="暂无拦截事件" /> : (
          <pre style={{ maxHeight: 240, overflow: "auto", fontSize: 12 }}>{JSON.stringify(events, null, 2)}</pre>
        )}
      </Card>
      <Modal
        open={creating || editing !== null}
        title={creating ? "新增规则" : "编辑规则"}
        onOk={save}
        onCancel={() => { setCreating(false); setEditing(null); }}
        destroyOnClose
      >
        <Form form={form} layout="vertical">
          <Form.Item name="name" label="名称" rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="category" label="类目" rules={[{ required: true }]}><Select options={CATEGORIES} /></Form.Item>
          <Form.Item name="pattern" label="匹配模式（正则或 IP/前缀）" rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="priority" label="优先级（大者优先）" initialValue={100}><InputNumber min={0} max={9999} /></Form.Item>
          <Form.Item name="enabled" label="启用" initialValue={true} valuePropName="checked"><Switch /></Form.Item>
        </Form>
      </Modal>
    </div>
  );
}
