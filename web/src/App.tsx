import { useEffect, useState } from "react";
import { Form, Input, Layout, Menu, Modal, Typography, Button, message } from "antd";
import { Outlet, useLocation, useNavigate } from "react-router-dom";
import { api, logout, me, type AuthUser } from "./api/client";

const { Header, Sider, Content } = Layout;

// Nav mirrors the prototype's four groups; keys map to routes.
const NAV = [
  { key: "g-console", label: "中控", children: [
    { key: "/dash", label: "总览" },
    { key: "/nodes", label: "多机中控" },
    { key: "/term", label: "终端" },
    { key: "/monitor", label: "监控告警" },
  ]},
  { key: "g-site", label: "站点与应用", children: [
    { key: "/site", label: "网站" },
    { key: "/db", label: "数据库" },
    { key: "/db-replica", label: "主从复制" },
    { key: "/files", label: "文件" },
    { key: "/ftp", label: "FTP" },
    { key: "/cron", label: "计划任务" },
    { key: "/env", label: "环境变量" },
    { key: "/docker", label: "Docker 管理" },
    { key: "/apps", label: "应用部署" },
    { key: "/k8s", label: "K8s 集群" },
    { key: "/webstats", label: "网站统计" },
    { key: "/backup", label: "备份还原" },
    { key: "/migrate", label: "一键迁移" },
    { key: "/lb", label: "负载均衡" },
    { key: "/ssl", label: "SSL 证书" },
  ]},
  { key: "g-sec", label: "安全", children: [
    { key: "/waf", label: "WAF 防火墙" },
    { key: "/fw", label: "主机防火墙" },
    { key: "/users", label: "用户管理" },
    { key: "/audit", label: "权限与审计" },
    { key: "/totp", label: "两步验证" },
  ]},
  { key: "g-ops", label: "运维", children: [
    { key: "/ai", label: "AI 运维" },
    { key: "/upgrade", label: "升级与规则包" },
    { key: "/push", label: "告警推送" },
    { key: "/logclean", label: "日志清理" },
    { key: "/settings", label: "系统设置" },
  ]},
];

// 改密码（backlog F2）：真连 POST /api/auth/change-password，旧密码校验 + 新密码 ≥8 位。
function ChangePasswordModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [form] = Form.useForm();
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();

  const submit = async () => {
    const v = await form.validateFields();
    setBusy(true);
    try {
      await api("/api/auth/change-password", {
        method: "POST",
        body: JSON.stringify({ oldPassword: v.oldPassword, newPassword: v.newPassword }),
      });
      msg.success("密码已修改，请用新密码重新登录");
      form.resetFields();
      onClose();
      setTimeout(() => { void logout().then(() => window.location.reload()); }, 1200);
    } catch (e) {
      msg.error(`修改失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      title="修改密码"
      open={open}
      onCancel={onClose}
      onOk={() => void submit()}
      confirmLoading={busy}
      okText="修改"
      destroyOnHidden
    >
      {contextHolder}
      <Form form={form} layout="vertical">
        <Form.Item name="oldPassword" label="当前密码" rules={[{ required: true, message: "请输入当前密码" }]}>
          <Input.Password />
        </Form.Item>
        <Form.Item name="newPassword" label="新密码" rules={[
          { required: true, message: "请输入新密码" },
          { min: 8, message: "至少 8 位" },
        ]}>
          <Input.Password />
        </Form.Item>
        <Form.Item name="confirm" label="确认新密码" dependencies={["newPassword"]} rules={[
          { required: true, message: "请再次输入新密码" },
          ({ getFieldValue }) => ({
            validator: (_, val) => (val === getFieldValue("newPassword") ? Promise.resolve() : Promise.reject(new Error("两次输入不一致"))),
          }),
        ]}>
          <Input.Password />
        </Form.Item>
      </Form>
    </Modal>
  );
}

export default function AppShell({ user, onLogout }: { user: AuthUser; onLogout: () => void }) {
  const nav = useNavigate();
  const loc = useLocation();
  const [collapsed, setCollapsed] = useState(false);
  const [pwdOpen, setPwdOpen] = useState(false);

  return (
    <Layout style={{ minHeight: "100vh" }}>
      <Sider collapsible collapsed={collapsed} onCollapse={setCollapsed}>
        <div style={{ color: "#fff", textAlign: "center", padding: 16, fontWeight: 700 }}>TaoOps</div>
        <Menu theme="dark" mode="inline" selectedKeys={[loc.pathname]} items={NAV}
          onClick={({ key }) => { if (String(key).startsWith("/")) nav(String(key)); }} />
      </Sider>
      <Layout>
        <Header style={{ background: "#fff", display: "flex", justifyContent: "space-between", alignItems: "center", paddingInline: 24 }}>
          <Typography.Text strong>TaoOps 服务器运维面板</Typography.Text>
          <span>
            <Typography.Text type="secondary" style={{ marginRight: 12 }}>{user.username} · {user.role}</Typography.Text>
            <Button size="small" style={{ marginRight: 8 }} onClick={() => setPwdOpen(true)}>改密码</Button>
            <Button size="small" onClick={async () => { await logout(); onLogout(); }}>退出</Button>
          </span>
        </Header>
        <Content style={{ margin: 16 }}>
          <Outlet />
        </Content>
      </Layout>
      <ChangePasswordModal open={pwdOpen} onClose={() => setPwdOpen(false)} />
    </Layout>
  );
}

export function useAuthGate(): { state: "loading" | "anon" | "authed"; user: AuthUser | null; refresh: () => void } {
  const [state, setState] = useState<"loading" | "anon" | "authed">("loading");
  const [user, setUser] = useState<AuthUser | null>(null);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    const t = localStorage.getItem("taoops_token");
    if (!t) { setState("anon"); return; }
    me().then((u) => { setUser(u); setState("authed"); })
      .catch(() => { localStorage.removeItem("taoops_token"); setState("anon"); });
  }, [tick]);

  return { state, user, refresh: () => setTick((x) => x + 1) };
}
