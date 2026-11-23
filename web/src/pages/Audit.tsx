import { useEffect, useState } from "react";
import { Button, Card, Table, Tabs, Tag, Typography, message } from "antd";
import { api } from "../api/client";

interface AuditEntry {
  seq: number;
  tsMs: number;
  user: string;
  action: string;
  target?: string;
  detail?: string;
}

interface AuthUserRow {
  username: string;
  role: string;
}

interface LockoutRow {
  username: string;
  failCount: number;
  lockedUntilMs: number;
}

interface SecEvent {
  nodeId: string;
  tsMs: number;
  kind: string;
  source?: string;
}

export default function AuditPage() {
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [users, setUsers] = useState<AuthUserRow[]>([]);
  const [lockouts, setLockouts] = useState<LockoutRow[]>([]);
  const [secEvents, setSecEvents] = useState<SecEvent[]>([]);
  const [chain, setChain] = useState<{ valid: boolean; broken_at_seq: number | null } | null>(null);
  const [msg, contextHolder] = message.useMessage();

  useEffect(() => {
    api<AuditEntry[]>("/api/audit").then(setEntries).catch(() => {});
    api<AuthUserRow[]>("/api/auth/users").then(setUsers).catch(() => {});
    api<LockoutRow[]>("/api/auth/lockouts").then(setLockouts).catch(() => {});
    api<SecEvent[]>("/api/sec/events").then(setSecEvents).catch(() => {});
    api<{ valid: boolean; broken_at_seq: number | null }>("/api/audit/verify").then(setChain).catch(() => {});
  }, []);

  const auditTable = (
    <>
      <Button size="small" style={{ marginBottom: 8 }} onClick={() => window.open("/api/audit/export?format=csv")}>导出 CSV</Button>
      <Table
        rowKey="seq" size="small" pagination={{ pageSize: 10 }} dataSource={entries}
        columns={[
          { title: "#", dataIndex: "seq", width: 60 },
          { title: "时间", dataIndex: "tsMs", render: (t: number) => new Date(t).toLocaleString() },
          { title: "用户", dataIndex: "user" },
          { title: "操作", dataIndex: "action" },
          { title: "对象", dataIndex: "target" },
        ]}
      />
    </>
  );

  const usersTable = (
    <Table
      rowKey="username" size="small" pagination={false} dataSource={users}
      columns={[
        { title: "用户名", dataIndex: "username" },
        { title: "角色", dataIndex: "role", render: (r: string) => <Tag color={r === "Admin" ? "red" : "blue"}>{r}</Tag> },
      ]}
    />
  );

  const lockoutTable = (
    <Table
      rowKey="username" size="small" pagination={false} dataSource={lockouts}
      columns={[
        { title: "用户名", dataIndex: "username" },
        { title: "失败次数", dataIndex: "failCount" },
        { title: "锁定至", dataIndex: "lockedUntilMs", render: (t: number) => (t > Date.now() ? new Date(t).toLocaleString() : "已解除") },
      ]}
    />
  );

  const secTable = (
    <Table
      rowKey={(r: SecEvent) => `${r.nodeId}-${r.tsMs}-${r.kind}`} size="small" pagination={{ pageSize: 10 }} dataSource={secEvents}
      columns={[
        { title: "节点", dataIndex: "nodeId" },
        { title: "时间", dataIndex: "tsMs", render: (t: number) => new Date(t).toLocaleString() },
        { title: "类型", dataIndex: "kind" },
        { title: "来源", dataIndex: "source" },
      ]}
    />
  );


  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>权限与审计</Typography.Title>
      <Card size="small" style={{ marginBottom: 12 }}>
        审计链完整性：
        {chain === null ? "校验中…" : chain.valid
          ? <Tag color="green">哈希链完整</Tag>
          : <Tag color="red">链断裂于 #{chain.broken_at_seq}</Tag>}
        <Button size="small" style={{ marginLeft: 12 }} onClick={async () => {
          try { setChain(await api("/api/audit/verify")); msg.success("已复验"); } catch { msg.error("复验失败"); }
        }}>重新校验</Button>
      </Card>
      <Tabs
        items={[
          { key: "audit", label: "操作审计", children: auditTable },
          { key: "users", label: "用户与角色", children: usersTable },
          { key: "lockouts", label: "登录锁定", children: lockoutTable },
          { key: "sec", label: "防爆破事件", children: secTable },
        ]}
      />
    </div>
  );
}
