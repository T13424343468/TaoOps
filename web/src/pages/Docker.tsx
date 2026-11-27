import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, Modal, Popconfirm, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// Docker 管理页（真连 POST /api/nodes/{id}/docker，契约 §6 + backlog A）。
// 容器生命周期/日志/命令、镜像拉取/删除、镜像加速器配置（daemon.json，
// 改后需重启 Docker 生效）、私有仓库登录（密码仅提交一次，不回显不落审计）。

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

interface ContainerRow {
  id: string;
  names: string[];
  image: string;
  state: string;
  status: string;
  ports?: string;
}

interface ImageRow {
  repo: string;
  tag: string;
  id: string;
  size: string;
  created: string;
}

// backlog D1：拉取进度结构化（Output JSON）。
interface PullLayer {
  id: string;
  status: string;
  current_bytes?: number;
  total_bytes?: number;
}

interface PullProgress {
  layers: PullLayer[];
  digest?: string;
  status?: string;
  raw?: string;
}

const fmtBytes = (b?: number) => {
  if (b === undefined || b === null) return "-";
  if (b >= 1_000_000_000) return `${(b / 1_000_000_000).toFixed(1)}GB`;
  if (b >= 1_000_000) return `${(b / 1_000_000).toFixed(1)}MB`;
  if (b >= 1_000) return `${(b / 1_000).toFixed(1)}kB`;
  return `${b}B`;
};

async function dockerOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/docker`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 30000, ...req }),
  });
}

const stateTag: Record<string, React.ReactNode> = {
  running: <Tag color="green">运行中</Tag>,
  exited: <Tag color="default">已退出</Tag>,
  paused: <Tag color="orange">已暂停</Tag>,
  restarting: <Tag color="blue">重启中</Tag>,
};

export default function DockerPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [containers, setContainers] = useState<ContainerRow[]>([]);
  const [images, setImages] = useState<ImageRow[]>([]);
  const [mirrors, setMirrors] = useState<string[]>([]);
  const [registries, setRegistries] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [pullRef, setPullRef] = useState("");
  const [mirrorAddr, setMirrorAddr] = useState("");
  const [logsFor, setLogsFor] = useState<ContainerRow | null>(null);
  const [logsText, setLogsText] = useState("");
  const [execFor, setExecFor] = useState<ContainerRow | null>(null);
  const [execCmd, setExecCmd] = useState("");
  const [execOut, setExecOut] = useState("");
  const [loginForm] = Form.useForm();
  const [msg, contextHolder] = message.useMessage();
  // backlog D1：拉取进度结构化展示。
  const [pullProgress, setPullProgress] = useState<PullProgress | null>(null);

  const loadNodes = useCallback(async () => {
    const list = await api<NodeView[]>("/api/nodes");
    setNodes(list);
    setNodeId((cur) => cur || list[0]?.nodeId || "");
  }, []);

  const loadAll = useCallback(async (id: string) => {
    if (!id) return;
    try {
      const [c, i, ml, rg] = await Promise.all([
        dockerOp(id, { op: "list" }),
        dockerOp(id, { op: "image_list" }),
        dockerOp(id, { op: "mirror_list" }),
        dockerOp(id, { op: "registry_list" }),
      ]);
      const parse = <T,>(r: CmdOutcome, key: string): T[] => {
        if (r.exit_code !== 0) return [];
        try {
          const o = JSON.parse(r.output) as Record<string, T[]>;
          return o[key] ?? [];
        } catch { return []; }
      };
      setContainers(parse<ContainerRow>(c, "containers"));
      setImages(parse<ImageRow>(i, "images"));
      setMirrors(parse<string>(ml, "mirrors"));
      setRegistries(parse<string>(rg, "registries"));
    } catch (e) {
      msg.error(`加载 Docker 数据失败：${(e as Error).message}`);
    }
  }, [msg]);

  useEffect(() => { void loadNodes(); }, [loadNodes]);
  useEffect(() => { void loadAll(nodeId); }, [nodeId, loadAll]);

  const run = async (req: Record<string, unknown>, ok: string) => {
    if (!nodeId) { msg.warning("先选择节点"); return; }
    setBusy(true);
    try {
      const r = await dockerOp(nodeId, req);
      if (r.exit_code !== 0) { msg.error(r.output); return false; }
      msg.success(ok);
      await loadAll(nodeId);
      return true;
    } catch (e) {
      msg.error(`操作失败：${(e as Error).message}`);
      return false;
    } finally {
      setBusy(false);
    }
  };

  const showLogs = async (c: ContainerRow) => {
    setLogsFor(c);
    setLogsText("加载中…");
    const name = c.names[0] ?? c.id;
    const r = await dockerOp(nodeId, { op: "logs", name, tail: 200 });
    setLogsText(r.exit_code === 0 ? r.output : r.output || "读取失败");
  };

  const runExec = async () => {
    if (!execFor || !execCmd.trim()) return;
    setExecOut("执行中…");
    const r = await dockerOp(nodeId, { op: "terminal", name: execFor.names[0] ?? execFor.id, command: execCmd });
    setExecOut(r.exit_code === 0 ? r.output : `${r.output}\n（退出码 ${r.exit_code}）`);
  };

  const addMirror = async () => {
    const addr = mirrorAddr.trim();
    if (!addr) return;
    if (await run({ op: "mirror_add", addr }, "加速器已写入（重启 Docker 后生效）")) setMirrorAddr("");
  };

  const login = async (values: { registry?: string; username: string; password: string }) => {
    const ok = await run({ op: "registry_login", registry: values.registry ?? "", username: values.username, password: values.password }, "登录成功");
    if (ok) loginForm.resetFields();
  };

  const name = (c: ContainerRow) => c.names[0] ?? c.id;

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>Docker 管理</Typography.Title>
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ minWidth: 220 }}
          value={nodeId || undefined}
          onChange={setNodeId}
          placeholder="选择节点"
          options={nodes.map((n) => ({ value: n.nodeId, label: n.hostname }))}
        />
      </Space>

      <Card size="small" title="容器" style={{ marginBottom: 12 }}>
        <Table
          rowKey={(r: ContainerRow) => r.id + (r.names[0] ?? "")}
          size="small"
          pagination={false}
          dataSource={containers}
          columns={[
            { title: "名称", dataIndex: "names", render: (v: string[]) => v[0] ?? "-" },
            { title: "镜像", dataIndex: "image" },
            { title: "状态", dataIndex: "state", render: (v: string) => stateTag[v] ?? v },
            { title: "详情", dataIndex: "status" },
            { title: "端口", dataIndex: "ports", render: (v: string) => v || "-" },
            {
              title: "操作",
              render: (_: unknown, c: ContainerRow) => (
                <Space wrap size={0}>
                  <Button size="small" type="link" disabled={busy} onClick={() => run({ op: "start", name: name(c) }, "已启动")}>启动</Button>
                  <Button size="small" type="link" disabled={busy} onClick={() => run({ op: "stop", name: name(c) }, "已停止")}>停止</Button>
                  <Button size="small" type="link" disabled={busy} onClick={() => run({ op: "restart", name: name(c) }, "已重启")}>重启</Button>
                  <Button size="small" type="link" onClick={() => void showLogs(c)}>日志</Button>
                  <Button size="small" type="link" onClick={() => { setExecFor(c); setExecCmd(""); setExecOut(""); }}>命令</Button>
                </Space>
              ),
            },
          ]}
        />
      </Card>

      <Card size="small" title="镜像" style={{ marginBottom: 12 }}>
        <Space style={{ marginBottom: 8 }} wrap>
          <Input
            style={{ width: 320 }}
            placeholder="镜像名，如 nginx:stable"
            value={pullRef}
            onChange={(e) => setPullRef(e.target.value)}
          />
          <Button type="primary" disabled={busy || !pullRef.trim()} onClick={async () => {
            setPullProgress(null);
            if (!nodeId) { msg.warning("先选择节点"); return; }
            setBusy(true);
            try {
              const r = await dockerOp(nodeId, { op: "image_pull", ref: pullRef.trim() });
              if (r.exit_code !== 0) { msg.error(r.output); return; }
              // backlog D1：Output 为结构化进度 JSON；旧版 Agent 原文输出降级为 raw 展示。
              try {
                const parsed = JSON.parse(r.output) as PullProgress;
                setPullProgress(Array.isArray(parsed?.layers) ? parsed : { layers: [], raw: r.output, status: "拉取完成" });
              } catch {
                setPullProgress({ layers: [], raw: r.output, status: "拉取完成" });
              }
              msg.success("拉取完成");
              await loadAll(nodeId);
            } catch (e) {
              msg.error(`操作失败：${(e as Error).message}`);
            } finally { setBusy(false); }
          }}>拉取</Button>
        </Space>
        {pullProgress && (
          <Alert
            style={{ marginTop: 8 }}
            type="success"
            showIcon
            message={`拉取进度：${pullProgress.status || "完成"}（${pullProgress.layers.length} 层）`}
            description={
              <div>
                <Table
                  rowKey="id"
                  size="small"
                  pagination={false}
                  dataSource={pullProgress.layers}
                  columns={[
                    { title: "层", dataIndex: "id" },
                    { title: "状态", dataIndex: "status" },
                    {
                      title: "进度",
                      render: (_, l: PullLayer) =>
                        l.total_bytes ? `${fmtBytes(l.current_bytes)} / ${fmtBytes(l.total_bytes)}` : "-",
                    },
                  ]}
                />
                {pullProgress.digest && <Typography.Text type="secondary">Digest: {pullProgress.digest}</Typography.Text>}
              </div>
            }
          />
        )}
        <Table
          rowKey={(r: ImageRow) => r.id + r.repo + r.tag}
          size="small"
          pagination={false}
          dataSource={images}
          columns={[
            { title: "仓库", dataIndex: "repo" },
            { title: "标签", dataIndex: "tag" },
            { title: "ID", dataIndex: "id" },
            { title: "大小", dataIndex: "size" },
            { title: "创建时间", dataIndex: "created" },
            {
              title: "操作",
              render: (_: unknown, r: ImageRow) => (
                <Popconfirm title={`删除镜像 ${r.repo}:${r.tag}？`} onConfirm={() => run({ op: "image_remove", ref: `${r.repo}:${r.tag}` }, "已删除")}>
                  <Button size="small" type="link" danger disabled={busy}>删除</Button>
                </Popconfirm>
              ),
            },
          ]}
        />
      </Card>

      <Card size="small" title="镜像加速器" style={{ marginBottom: 12 }}>
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 8 }}
          message="写入 /etc/docker/daemon.json（自动备份原文件）；改完点「重启 Docker」生效，运行中的容器会按重启策略自动拉起。"
        />
        <Space style={{ marginBottom: 8 }} wrap>
          {mirrors.length === 0 && <Typography.Text type="secondary">未配置加速器</Typography.Text>}
          {mirrors.map((m) => (
            <Tag key={m} closable onClose={() => run({ op: "mirror_remove", addr: m }, "已移除（重启 Docker 后生效）")}>{m}</Tag>
          ))}
        </Space>
        <Space wrap>
          <Input
            style={{ width: 360 }}
            placeholder="加速地址，如 https://docker.m.daocloud.io"
            value={mirrorAddr}
            onChange={(e) => setMirrorAddr(e.target.value)}
          />
          <Button disabled={busy || !mirrorAddr.trim()} onClick={addMirror}>添加</Button>
          <Popconfirm title="重启 Docker 守护进程？运行中的容器会按重启策略自动拉起。" onConfirm={() => run({ op: "daemon_restart" }, "Docker 已重启")}>
            <Button danger disabled={busy}>重启 Docker</Button>
          </Popconfirm>
        </Space>
      </Card>

      <Card size="small" title="私有仓库">
        <Space style={{ marginBottom: 8 }} wrap>
          {registries.length === 0 && <Typography.Text type="secondary">未登录任何仓库</Typography.Text>}
          {registries.map((r) => (
            <Tag key={r}>{r}</Tag>
          ))}
        </Space>
        <Form form={loginForm} layout="inline" onFinish={login} style={{ flexWrap: "wrap", rowGap: 8 }}>
          <Form.Item name="registry" rules={[{ pattern: /^[a-zA-Z0-9][a-zA-Z0-9._-]*(:[0-9]+)?(\/[a-zA-Z0-9._/-]+)?$/, message: "仓库地址格式不对" }]}>
            <Input style={{ width: 240 }} placeholder="仓库地址（留空为 Docker Hub）" />
          </Form.Item>
          <Form.Item name="username" rules={[{ required: true, message: "填用户名" }]}>
            <Input style={{ width: 160 }} placeholder="用户名" autoComplete="off" />
          </Form.Item>
          <Form.Item name="password" rules={[{ required: true, message: "填密码" }]}>
            <Input.Password style={{ width: 200 }} placeholder="密码（仅提交一次）" autoComplete="new-password" />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" disabled={busy}>登录</Button>
          </Form.Item>
        </Form>
        {registries.length > 0 && (
          <Popconfirm title="退出登录会删除该仓库的保存凭据，确认？" onConfirm={() => run({ op: "registry_logout", registry: registries[registries.length - 1] }, "已退出登录")}>
            <Button size="small" type="link" danger disabled={busy}>退出最近登录的仓库</Button>
          </Popconfirm>
        )}
      </Card>

      <Modal
        title={`容器日志：${logsFor?.names[0] ?? ""}`}
        open={!!logsFor}
        onCancel={() => setLogsFor(null)}
        footer={null}
        width={860}
      >
        <pre style={{ background: "#000", color: "#0f0", padding: 8, borderRadius: 4, maxHeight: 420, overflow: "auto", fontSize: 12, whiteSpace: "pre-wrap" }}>{logsText}</pre>
      </Modal>

      <Modal
        title={`容器内执行命令：${execFor?.names[0] ?? ""}`}
        open={!!execFor}
        onCancel={() => setExecFor(null)}
        footer={null}
        width={760}
      >
        <Space.Compact style={{ width: "100%", marginBottom: 8 }}>
          <Input
            value={execCmd}
            onChange={(e) => setExecCmd(e.target.value)}
            placeholder="如 ls /app"
            onPressEnter={() => void runExec()}
          />
          <Button type="primary" disabled={busy || !execCmd.trim()} onClick={() => void runExec()}>执行</Button>
        </Space.Compact>
        {execOut && <pre style={{ background: "#000", color: "#0f0", padding: 8, borderRadius: 4, maxHeight: 360, overflow: "auto", fontSize: 12, whiteSpace: "pre-wrap" }}>{execOut}</pre>}
      </Modal>
    </div>
  );
}
