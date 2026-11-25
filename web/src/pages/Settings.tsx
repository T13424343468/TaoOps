import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, InputNumber, Typography, message } from "antd";
import { api } from "../api/client";

// 系统设置页（backlog F4）：告警阈值可视化。GET 回显（登录即可），PUT 落盘（Admin only，后端校验 1-99 且 reset<critical）。

interface AlertThresholds {
  disk_critical_pct: number;
  disk_reset_pct: number;
}

export default function Settings() {
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [form] = Form.useForm();
  const [msg, contextHolder] = message.useMessage();

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const t = await api<AlertThresholds>("/api/settings/alerts");
      form.setFieldsValue(t);
    } catch (e) {
      msg.error(`加载失败：${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  }, [form, msg]);

  useEffect(() => { void load(); }, [load]);

  const save = async () => {
    const v = await form.validateFields();
    setSaving(true);
    try {
      await api("/api/settings/alerts", { method: "PUT", body: JSON.stringify(v) });
      msg.success("告警阈值已保存");
      await load();
    } catch (e) {
      msg.error(`保存失败：${(e as Error).message}`);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>系统设置</Typography.Title>
      <Card size="small" title="告警阈值" loading={loading} style={{ maxWidth: 560 }}>
        <Form form={form} layout="vertical">
          <Form.Item
            name="disk_critical_pct"
            label="磁盘用量告警阈值（%）"
            rules={[{ required: true, message: "请输入 1-99 之间的整数" }]}
            extra="磁盘占用达到该百分比时触发告警"
          >
            <InputNumber min={1} max={99} precision={0} style={{ width: 160 }} />
          </Form.Item>
          <Form.Item
            name="disk_reset_pct"
            label="告警恢复阈值（%）"
            rules={[{ required: true, message: "请输入 1-99 之间的整数" }]}
            extra="磁盘占用回落到该百分比以下时解除告警，须低于告警阈值"
          >
            <InputNumber min={1} max={99} precision={0} style={{ width: 160 }} />
          </Form.Item>
          <Button type="primary" loading={saving} onClick={() => void save()}>保存</Button>
        </Form>
        <Alert style={{ marginTop: 12 }} type="info" showIcon
          message="仅 Admin 可保存"
          description="阈值即时生效（热重载），无需重启面板；保存操作留审计。" />
      </Card>
    </div>
  );
}
