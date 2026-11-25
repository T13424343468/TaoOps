import { useState } from "react";
import { Alert, Button, Form, Input, Tabs, Typography } from "antd";
import { bootstrap, login, totpVerify, TotpRequiredError } from "../api/client";

// FR-A16 两步验证：密码正确且已启用 TOTP 时，登录返回 totp_required，
// 转入 6 位动态码（或恢复码）二次校验，通过后签发会话。
export default function LoginPage({ onLogin }: { onLogin: () => void }) {
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [totpUser, setTotpUser] = useState<string | null>(null);

  const doLogin = async (v: { username: string; password: string }) => {
    setBusy(true); setErr(null);
    try {
      await login(v.username, v.password);
      onLogin();
    } catch (e) {
      if (e instanceof TotpRequiredError) { setTotpUser(v.username); return; }
      const msg = String(e instanceof Error ? e.message : e);
      setErr(msg.includes("locked") ? "失败次数过多，账号已临时锁定，请稍后再试" : `登录失败：${msg}`);
    } finally { setBusy(false); }
  };

  const doTotp = async (v: { code: string }) => {
    if (!totpUser) return;
    setBusy(true); setErr(null);
    try {
      await totpVerify(totpUser, v.code.trim());
      onLogin();
    } catch (e) {
      const msg = String(e instanceof Error ? e.message : e);
      setErr(msg.includes("locked") ? "失败次数过多，账号已临时锁定，请稍后再试" : `二次校验失败：${msg}`);
    } finally { setBusy(false); }
  };

  const doBootstrap = async (v: { username: string; password: string }) => {
    setBusy(true); setErr(null);
    try {
      await bootstrap(v.username, v.password);
      await login(v.username, v.password);
      onLogin();
    } catch (e) {
      setErr(`初始化失败：${e instanceof Error ? e.message : e}`);
    } finally { setBusy(false); }
  };

  const totpForm = (
    <Form onFinish={doTotp} layout="vertical">
      <Typography.Paragraph type="secondary">
        账号已启用两步验证：请输入验证器 App 中的 6 位动态码（或一枚未使用的恢复码）。
      </Typography.Paragraph>
      <Form.Item name="code" label="动态码" rules={[{ required: true, message: "请输入 6 位动态码" }]}>
        <Input autoFocus inputMode="numeric" maxLength={10} placeholder="123456" />
      </Form.Item>
      <Button type="primary" htmlType="submit" block loading={busy}>验证并登录</Button>
      <Button block type="link" onClick={() => { setTotpUser(null); setErr(null); }}>返回重新登录</Button>
    </Form>
  );

  const loginForm = (
    <Form onFinish={doLogin} layout="vertical">
      <Form.Item name="username" label="用户名" rules={[{ required: true, message: "请输入用户名" }]}>
        <Input autoFocus />
      </Form.Item>
      <Form.Item name="password" label="密码" rules={[{ required: true, message: "请输入密码" }]}>
        <Input.Password />
      </Form.Item>
      <Button type="primary" htmlType="submit" block loading={busy}>登录</Button>
    </Form>
  );

  const bootstrapForm = (
    <Form onFinish={doBootstrap} layout="vertical">
      <Typography.Paragraph type="secondary">
        首次部署：创建管理员账号（仅当系统尚无用户时可用）。
      </Typography.Paragraph>
      <Form.Item name="username" label="管理员用户名" rules={[{ required: true }]}>
        <Input />
      </Form.Item>
      <Form.Item name="password" label="密码" rules={[{ required: true, min: 8, message: "至少 8 位" }]}>
        <Input.Password />
      </Form.Item>
      <Button type="primary" htmlType="submit" block loading={busy}>创建并登录</Button>
    </Form>
  );

  return (
    <div style={{ minHeight: "100vh", display: "flex", alignItems: "center", justifyContent: "center", background: "#f0f2f5" }}>
      <div style={{ width: 380, background: "#fff", padding: 32, borderRadius: 8 }}>
        <Typography.Title level={3} style={{ textAlign: "center" }}>TaoOps 控制台</Typography.Title>
        {err && <Alert type="error" message={err} style={{ marginBottom: 16 }} showIcon />}
        {totpUser ? (
          totpForm
        ) : (
          <Tabs items={[{ key: "login", label: "登录", children: loginForm }, { key: "bootstrap", label: "首次初始化", children: bootstrapForm }]} />
        )}
      </div>
    </div>
  );
}
