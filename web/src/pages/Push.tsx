import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, Switch, Table, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A20 自定义告警推送：渠道管理（Admin）、SSRF 白名单配置、测试推送、推送日志。
interface PushChannel {
  id: string;
  name: string;
  url: string;
  enabled: boolean;
  created_ms: number;
}

interface PushResult {
  channel_id: string;
  url: string;
  ok: boolean;
  status_code: number | null;
  error: string;
  attempts: number;
}

interface PushLogEntry {
  ts: number;
  channel_id: string;
  url: string;
  status_code: number | null;
  ok: boolean;
  error: string;
  attempt: number;
}

export default function PushPage() {
  const [channels, setChannels] = useState<PushChannel[]>([]);
  const [whitelist, setWhitelist] = useState<string[]>([]);
  const [logs, setLogs] = useState<PushLogEntry[]>([]);
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();
  const [channelForm] = Form.useForm();
  const [whitelistForm] = Form.useForm();

  const load = useCallback(async () => {
    try {
      setChannels(await api<PushChannel[]>("/api/push/channels"));
      setWhitelist(await api<string[]>("/api/push/whitelist"));
      setLogs(await api<PushLogEntry[]>("/api/push/log"));
    } catch (e) {
      msg.error(`推送配置加载失败：${(e as Error).message}`);
    }
  }, [msg]);

  useEffect(() => { void load(); }, [load]);

  const addChannel = async (values: { name: string; url: string }) => {
    setBusy(true);
    try {
      await api("/api/push/channels", {
        method: "POST",
        body: JSON.stringify({ name: values.name, url: values.url, enabled: true }),
      });
      msg.success("渠道已添加");
      channelForm.resetFields();
      void load();
    } catch (e) {
      msg.error(`添加失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const removeChannel = async (id: string) => {
    try {
      await api(`/api/push/channels/${encodeURIComponent(id)}`, { method: "DELETE" });
      msg.success("渠道已删除");
      void load();
    } catch (e) {
      msg.error(`删除失败：${(e as Error).message}`);
    }
  };

  const saveWhitelist = async (values: { entries: string }) => {
    setBusy(true);
    try {
      const entries = values.entries.split("\n").map((s) => s.trim()).filter(Boolean);
      await api("/api/push/whitelist", { method: "PUT", body: JSON.stringify({ entries }) });
      msg.success("白名单已更新（变更进审计）");
      void load();
    } catch (e) {
      msg.error(`保存失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const testPush = async (channelId: string) => {
    try {
      const results = await api<PushResult[]>("/api/push/test", {
        method: "POST",
        body: JSON.stringify({ channel_id: channelId }),
      });
      const r = results[0];
      if (r?.ok) msg.success(`测试推送成功（尝试 ${r.attempts} 次）`);
      else msg.error(`测试推送失败：${r?.error ?? "无结果"}`);
      void load();
    } catch (e) {
      msg.error(`测试失败：${(e as Error).message}`);
    }
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>告警推送</Typography.Title>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="critical 告警触发时向启用渠道 POST JSON；内网地址默认拒绝，白名单由管理员配置且变更进审计；失败重试 3 次（1s/2s/4s）。" />

      <Card size="small" title="推送渠道">
        <Table
          rowKey="id"
          size="small"
          pagination={false}
          dataSource={channels}
          columns={[
            { title: "名称", dataIndex: "name" },
            { title: "Webhook", dataIndex: "url", ellipsis: true },
            {
              title: "启用", dataIndex: "enabled",
              render: (v: boolean) => <Switch size="small" checked={v} disabled />,
            },
            {
              title: "操作",
              render: (_: unknown, c: PushChannel) => (
                <Button size="small" onClick={() => void testPush(c.id)}>测试推送</Button>
              ),
            },
            {
              title: "",
              render: (_: unknown, c: PushChannel) => (
                <Button size="small" danger onClick={() => void removeChannel(c.id)}>删除</Button>
              ),
            },
          ]}
          locale={{ emptyText: "（暂无渠道）" }}
        />
        <Form form={channelForm} layout="inline" onFinish={addChannel} style={{ marginTop: 12 }}>
          <Form.Item name="name" rules={[{ required: true, message: "名称必填" }]}>
            <Input placeholder="渠道名称" style={{ width: 160 }} />
          </Form.Item>
          <Form.Item name="url" rules={[{ required: true, message: "Webhook 必填" }]}>
            <Input placeholder="https://open.feishu.cn/hook/... （http(s)）" style={{ width: 380 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>添加渠道</Button>
          </Form.Item>
        </Form>
      </Card>

      <Card size="small" title="SSRF 白名单（内网地址放行，逐条一行）" style={{ marginTop: 12 }}>
        <Form form={whitelistForm} layout="inline" onFinish={saveWhitelist}
          initialValues={{ entries: (whitelist || []).join("\n") }}>
          <Form.Item name="entries">
            <Input.TextArea rows={3} placeholder={"10.20.0.0/16\nhook.internal.example"} style={{ width: 420 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>保存白名单</Button>
          </Form.Item>
        </Form>
      </Card>

      <Card size="small" title="推送日志" style={{ marginTop: 12 }}>
        <Table
          rowKey={(e) => `${e.ts}-${e.channel_id}-${e.attempt}`}
          size="small"
          pagination={{ pageSize: 10 }}
          dataSource={logs}
          columns={[
            { title: "时间", dataIndex: "ts", render: (v: number) => new Date(v).toLocaleString() },
            { title: "渠道", dataIndex: "channel_id" },
            { title: "URL", dataIndex: "url", ellipsis: true },
            { title: "状态码", dataIndex: "status_code", render: (v: number | null) => v ?? "-" },
            { title: "尝试", dataIndex: "attempt" },
            {
              title: "结果", dataIndex: "ok",
              render: (ok: boolean, e: PushLogEntry) => ok ? "成功" : (e.error || "失败"),
            },
          ]}
          locale={{ emptyText: "（暂无推送记录）" }}
        />
      </Card>
    </div>
  );
}
