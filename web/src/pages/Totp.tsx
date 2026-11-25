import { useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, Space, Tag, Typography, message } from "antd";
import { api, totpEnable, totpRegenerate, totpSetup, totpStatus } from "../api/client";

// FR-A16 两步验证设置：绑定（密码复验 → 密钥/otpauth URI → 6 位码确认 →
// 一次性展示 10 枚恢复码）与恢复码重生成（旧码全部失效）。全部真连端点。
interface MeResp { user?: string; role?: string }

export default function TotpPage() {
  const [me, setMe] = useState<MeResp>({});
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [pending, setPending] = useState<{ secret: string; otpauthUri: string } | null>(null);
  const [setupPassword, setSetupPassword] = useState("");
  const [codes, setCodes] = useState<string[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();

  const username = me.user ?? "";

  const refresh = async () => {
    if (!username) return;
    try {
      const r = await totpStatus(username);
      setEnabled(r.enabled);
      if (!r.enabled) setPending(null);
    } catch (e) {
      msg.error(`查询状态失败：${(e as Error).message}`);
    }
  };

  useEffect(() => {
    api<MeResp>("/api/auth/me").then(setMe).catch(() => {});
  }, []);
  useEffect(() => { void refresh(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [username]);

  const doSetup = async (v: { password: string }) => {
    setBusy(true);
    try {
      const r = await totpSetup(username, v.password);
      setSetupPassword(v.password);
      setPending({ secret: r.secret, otpauthUri: r.otpauth_uri });
      msg.success("已生成绑定密钥，请用验证器 App 扫码或手动录入");
    } catch (e) {
      msg.error(`绑定失败：${(e as Error).message}`);
    } finally { setBusy(false); }
  };

  const doEnable = async (v: { code: string }) => {
    if (!pending) return;
    setBusy(true);
    try {
      const r = await totpEnable(username, v.code.trim(), setupPassword);
      setCodes(r.recovery_codes);
      setPending(null);
      await refresh();
      msg.success("两步验证已启用，请妥善保存恢复码");
    } catch (e) {
      msg.error(`确认失败：${(e as Error).message}`);
    } finally { setBusy(false); }
  };

  const doRegenerate = async (v: { password: string }) => {
    setBusy(true);
    try {
      const r = await totpRegenerate(username, v.password);
      setCodes(r.recovery_codes);
      msg.success("已重新生成恢复码，旧码全部失效");
    } catch (e) {
      msg.error(`重生成失败：${(e as Error).message}`);
    } finally { setBusy(false); }
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>两步验证（TOTP）</Typography.Title>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="本地 RFC 6238 动态码：绑定需密码复验；登录时在密码之后输入验证器 6 位码；时钟窗 ±1 个 30s 周期；连续 5 次校验失败锁定 5 分钟；恢复码仅显示一次、用后即焚，重新生成后旧码全部失效。" />

      <Card size="small" title="当前状态" style={{ marginBottom: 12 }}>
        <Space>
          {enabled === null && <Typography.Text type="secondary">查询中…</Typography.Text>}
          {enabled === true && <><Tag color="green">已启用</Tag><Typography.Text>登录时需输入动态码</Typography.Text></>}
          {enabled === false && <><Tag color="red">未启用</Tag><Typography.Text>建议立即绑定以提升账号安全</Typography.Text></>}
          <Button size="small" onClick={() => void refresh()}>刷新</Button>
        </Space>
      </Card>

      {enabled === false && (
        <Card size="small" title="1. 生成绑定密钥（需密码复验）" style={{ marginBottom: 12 }}>
          <Form layout="inline" onFinish={doSetup}>
            <Form.Item name="password" rules={[{ required: true, message: "请输入当前密码" }]}>
              <Input.Password placeholder="当前密码" style={{ width: 220 }} />
            </Form.Item>
            <Form.Item>
              <Button type="primary" htmlType="submit" loading={busy}>生成密钥</Button>
            </Form.Item>
          </Form>
          {pending && (
            <div style={{ marginTop: 12 }}>
              <Typography.Paragraph>
                密钥：<Typography.Text code copyable>{pending.secret}</Typography.Text>
              </Typography.Paragraph>
              <Typography.Paragraph type="secondary" style={{ wordBreak: "break-all" }}>
                otpauth URI：<Typography.Text code>{pending.otpauthUri}</Typography.Text>
              </Typography.Paragraph>
              <Typography.Paragraph type="warning">
                请在验证器 App（如 Google Authenticator）中「手动录入密钥」或扫描该 URI 生成的二维码，然后输入 6 位码确认绑定。
              </Typography.Paragraph>
            </div>
          )}
        </Card>
      )}

      {pending && (
        <Card size="small" title="2. 输入 6 位码确认绑定" style={{ marginBottom: 12 }}>
          <Form layout="inline" onFinish={doEnable}>
            <Form.Item name="code" rules={[{ required: true, message: "请输入 6 位动态码" }]}>
              <Input inputMode="numeric" maxLength={6} placeholder="123456" style={{ width: 160 }} />
            </Form.Item>
            <Form.Item>
              <Button type="primary" htmlType="submit" loading={busy}>确认绑定</Button>
            </Form.Item>
          </Form>
        </Card>
      )}

      {codes && (
        <Card size="small" title="恢复码（仅此一次展示，请立即保存）" style={{ marginBottom: 12 }}>
          <Alert type="warning" showIcon style={{ marginBottom: 8 }}
            message="每枚恢复码只能使用一次；页面关闭后不再显示。重新生成会使下列全部恢复码失效。" />
          <Typography.Paragraph>
            <Typography.Text code>{codes.join("  ·  ")}</Typography.Text>
          </Typography.Paragraph>
        </Card>
      )}

      {enabled === true && (
        <Card size="small" title="重新生成恢复码（旧码全部失效，需密码复验）">
          <Form layout="inline" onFinish={doRegenerate}>
            <Form.Item name="password" rules={[{ required: true, message: "请输入当前密码" }]}>
              <Input.Password placeholder="当前密码" style={{ width: 220 }} />
            </Form.Item>
            <Form.Item>
              <Button danger type="primary" htmlType="submit" loading={busy}>重新生成 10 枚恢复码</Button>
            </Form.Item>
          </Form>
        </Card>
      )}
    </div>
  );
}
