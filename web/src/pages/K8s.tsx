import { useCallback, useEffect, useState } from "react";
import {
  Alert, Button, Card, Form, Input, InputNumber, Modal, Select, Space, Table,
  Tabs, Tag, Typography, message,
} from "antd";
import { api } from "../api/client";

// FR-A28 K8s 集成：集群接入（kubeconfig 粘贴，连接测试通过才接入）、
// 节点/工作负载/Pod/事件只读视图、扩缩容/重启/滚动更新进度、模板一键部署到集群。
interface ClusterMeta {
  name: string;
  server: string;
  version: string;
  added_ms: number;
}

interface NodeSummary {
  name: string;
  status: string;
  version: string;
  cpuCores: number;
  memory: string;
  pods: number;
}

interface WorkloadSummary {
  kind: string;
  name: string;
  namespace: string;
  ready: number;
  desired: number;
  image: string | null;
  ageMs: number | null;
}

interface PodSummary {
  name: string;
  namespace: string;
  node: string;
  phase: string;
  restarts: number;
  ready: boolean;
  ageMs: number | null;
  reason: string | null;
}

interface EventSummary {
  namespace: string;
  type: string;
  reason: string;
  object: string;
  message: string;
  lastTsMs: number | null;
}

interface K8sTemplate {
  id: string;
  name: string;
  arch: string;
  kind: string;
}

// backlog C1：Pod/容器级资源用量（metrics.k8s.io，未装 metrics-server 时降级）。
interface ContainerMetric {
  name: string;
  cpu_milli: number;
  mem_mib: number;
}

interface PodMetricRow {
  namespace: string;
  name: string;
  cpu_milli: number;
  mem_mib: number;
  containers: ContainerMetric[];
}

interface PodMetricsResult {
  available: boolean;
  note: string | null;
  pods: PodMetricRow[];
}

interface RolloutInfo {
  name: string;
  desired: number;
  updated: number;
  available: number;
  complete: boolean;
  message: string | null;
}

function age(ms: number | null): string {
  if (ms === null) return "-";
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${Math.floor(s / 60)}m`;
  if (s < 86400) return `${Math.floor(s / 3600)}h`;
  return `${Math.floor(s / 86400)}d`;
}

export default function K8sPage() {
  const [clusters, setClusters] = useState<ClusterMeta[]>([]);
  const [current, setCurrent] = useState<string>("");
  const [nodes, setNodes] = useState<NodeSummary[]>([]);
  const [workloads, setWorkloads] = useState<WorkloadSummary[]>([]);
  const [pods, setPods] = useState<PodSummary[]>([]);
  const [events, setEvents] = useState<EventSummary[]>([]);
  const [templates, setTemplates] = useState<K8sTemplate[]>([]);
  const [podMetrics, setPodMetrics] = useState<PodMetricsResult | null>(null);
  const [logText, setLogText] = useState<string>("");
  const [rollout, setRollout] = useState<RolloutInfo | null>(null);
  const [addOpen, setAddOpen] = useState(false);
  const [deployOpen, setDeployOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();
  const [addForm] = Form.useForm();
  const [deployForm] = Form.useForm();

  const loadClusters = useCallback(async () => {
    try {
      const list = await api<ClusterMeta[]>("/api/k8s/clusters");
      setClusters(list);
      setCurrent((c) => c || list[0]?.name || "");
    } catch (e) {
      msg.error(`集群列表加载失败：${(e as Error).message}`);
    }
  }, [msg]);

  const loadClusterData = useCallback(async (cluster: string) => {
    if (!cluster) return;
    try {
      const [n, w, p, ev, t] = await Promise.all([
        api<NodeSummary[]>(`/api/k8s/${cluster}/nodes`),
        api<WorkloadSummary[]>(`/api/k8s/${cluster}/workloads`),
        api<PodSummary[]>(`/api/k8s/${cluster}/pods`),
        api<EventSummary[]>(`/api/k8s/${cluster}/events`),
        api<K8sTemplate[]>(`/api/k8s/${cluster}/ops`, { method: "POST", body: JSON.stringify({ op: "templates" }) }),
      ]);
      setNodes(n);
      setWorkloads(w);
      setPods(p);
      setEvents(ev);
      setTemplates(t);
      // 资源用量独立降级：metrics-server 未装不阻塞主视图。
      api<PodMetricsResult>(`/api/k8s/${cluster}/metrics`)
        .then(setPodMetrics)
        .catch(() => setPodMetrics(null));
    } catch (e) {
      msg.error(`集群数据加载失败：${(e as Error).message}`);
    }
  }, [msg]);

  useEffect(() => { loadClusters(); }, [loadClusters]);
  useEffect(() => { loadClusterData(current); }, [current, loadClusterData]);

  const addCluster = async (values: { name: string; kubeconfig: string }) => {
    setBusy(true);
    try {
      await api("/api/k8s/clusters", {
        method: "POST",
        body: JSON.stringify({ op: "add", name: values.name, kubeconfig: values.kubeconfig }),
      });
      msg.success(`集群 ${values.name} 接入成功`);
      setAddOpen(false);
      addForm.resetFields();
      await loadClusters();
      setCurrent(values.name);
    } catch (e) {
      msg.error(`接入失败：${(e as Error).message}`);
    } finally { setBusy(false); }
  };

  const removeCluster = (name: string) => {
    Modal.confirm({
      title: `删除集群 ${name}？`,
      content: "将同时删除保存的 kubeconfig，操作不可恢复。",
      okText: "删除",
      okType: "danger",
      onOk: async () => {
        try {
          await api(`/api/k8s/clusters/${name}?confirm=true`, { method: "DELETE" });
          msg.success("集群已删除");
          setCurrent("");
          await loadClusters();
        } catch (e) {
          msg.error(`删除失败：${(e as Error).message}`);
        }
      },
    });
  };

  const scale = async (w: WorkloadSummary) => {
    let replicas = w.desired;
    Modal.confirm({
      title: `调整 ${w.name} 副本数`,
      content: (
        <InputNumber min={0} max={100} defaultValue={w.desired}
          onChange={(v) => { replicas = Number(v); }} style={{ marginTop: 8 }} />
      ),
      onOk: async () => {
        try {
          await api(`/api/k8s/${current}/ops`, {
            method: "POST",
            body: JSON.stringify({ op: "scale", namespace: w.namespace, deployment: w.name, replicas: String(replicas) }),
          });
          msg.success("扩缩容已提交");
          loadClusterData(current);
        } catch (e) {
          msg.error(`操作失败：${(e as Error).message}`);
        }
      },
    });
  };

  const restart = (w: WorkloadSummary) => {
    Modal.confirm({
      title: `滚动重启 ${w.name}？`,
      content: "将触发滚动更新，逐批替换 Pod。",
      onOk: async () => {
        try {
          await api(`/api/k8s/${current}/ops`, {
            method: "POST",
            body: JSON.stringify({ op: "restart", namespace: w.namespace, deployment: w.name }),
          });
          msg.success("重启已提交");
          loadClusterData(current);
        } catch (e) {
          msg.error(`操作失败：${(e as Error).message}`);
        }
      },
    });
  };

  const checkRollout = async (w: WorkloadSummary) => {
    try {
      setRollout(await api<RolloutInfo>(`/api/k8s/${current}/ops`, {
        method: "POST",
        body: JSON.stringify({ op: "rollout_status", namespace: w.namespace, deployment: w.name }),
      }));
    } catch (e) {
      msg.error(`查询失败：${(e as Error).message}`);
    }
  };

  const viewLogs = async (p: PodSummary) => {
    try {
      const text = await api<string>(`/api/k8s/${current}/logs?ns=${encodeURIComponent(p.namespace)}&pod=${encodeURIComponent(p.name)}`);
      setLogText(text || "（无日志输出）");
    } catch (e) {
      setLogText(`日志获取失败：${(e as Error).message}`);
    }
  };

  const deploy = async (values: { template: string; app: string; ns: string; replicas: number }) => {
    setBusy(true);
    try {
      const r = await api<{ app: string; deployments: number; services: number }>(`/api/k8s/${current}/ops`, {
        method: "POST",
        body: JSON.stringify({
          op: "deploy", template: values.template, name: values.app,
          namespace: values.ns, replicas: String(values.replicas),
        }),
      });
      msg.success(`部署成功：${r.deployments} 个 Deployment、${r.services} 个 Service`);
      setDeployOpen(false);
      deployForm.resetFields();
      loadClusterData(current);
    } catch (e) {
      msg.error(`部署失败：${(e as Error).message}`);
    } finally { setBusy(false); }
  };

  const removeApp = (w: WorkloadSummary) => {
    Modal.confirm({
      title: `卸载 ${w.name}？`,
      content: "按应用标签删除集群内 Deployment 与 Service，操作不可恢复。",
      okText: "卸载",
      okType: "danger",
      onOk: async () => {
        try {
          await api(`/api/k8s/${current}/ops`, {
            method: "POST",
            body: JSON.stringify({ op: "remove", namespace: w.namespace, name: w.name, confirm: true }),
          });
          msg.success("卸载完成");
          loadClusterData(current);
        } catch (e) {
          msg.error(`卸载失败：${(e as Error).message}`);
        }
      },
    });
  };

  return (
    <Space direction="vertical" size="middle" style={{ width: "100%" }}>
      {contextHolder}
      <Card
        title="K8s 集群"
        extra={
          <Space>
            <Button type="primary" onClick={() => setAddOpen(true)}>接入集群</Button>
            <Button onClick={() => setDeployOpen(true)} disabled={!current}>一键部署</Button>
            <Button onClick={() => loadClusterData(current)} disabled={!current}>刷 新</Button>
          </Space>
        }
      >
        <Table
          rowKey="name"
          size="small"
          pagination={false}
          dataSource={clusters}
          onRow={(rec) => ({ onClick: () => setCurrent(rec.name), style: { cursor: "pointer" } })}
          rowClassName={(rec) => (rec.name === current ? "ant-table-row-selected" : "")}
          columns={[
            { title: "集群", dataIndex: "name" },
            { title: "API Server", dataIndex: "server" },
            { title: "版本", dataIndex: "version" },
            {
              title: "操作",
              render: (_, rec: ClusterMeta) => (
                <Space>
                  <Button size="small" onClick={(e) => { e.stopPropagation(); setCurrent(rec.name); }}>查看</Button>
                  <Button size="small" danger onClick={(e) => { e.stopPropagation(); removeCluster(rec.name); }}>删除</Button>
                </Space>
              ),
            },
          ]}
        />
        {clusters.length === 0 && (
          <Alert type="info" showIcon style={{ marginTop: 12 }}
            message="尚未接入集群"
            description="点击「接入集群」粘贴 kubeconfig（只保存在中控，权限 600，绝不下发节点）。连接测试通过才算接入成功。" />
        )}
      </Card>

      {current && (
        <Card title={`集群视图：${current}`}>
          <Tabs items={[
            {
              key: "nodes", label: "节点",
              children: (
                <Table rowKey="name" size="small" pagination={false} dataSource={nodes}
                  columns={[
                    { title: "名称", dataIndex: "name" },
                    { title: "状态", dataIndex: "status", render: (s: string) => <Tag color={s === "Ready" ? "green" : "red"}>{s}</Tag> },
                    { title: "Kubelet 版本", dataIndex: "version" },
                    { title: "CPU 核", dataIndex: "cpuCores" },
                    { title: "内存", dataIndex: "memory" },
                  ]} />
              ),
            },
            {
              key: "workloads", label: "工作负载",
              children: (
                <Table rowKey={(r) => `${r.kind}/${r.namespace}/${r.name}`} size="small" pagination={false}
                  dataSource={workloads}
                  columns={[
                    { title: "类型", dataIndex: "kind" },
                    { title: "名称", dataIndex: "name" },
                    { title: "命名空间", dataIndex: "namespace" },
                    { title: "就绪/期望", render: (_, w: WorkloadSummary) => `${w.ready}/${w.desired}` },
                    { title: "镜像", dataIndex: "image", ellipsis: true },
                    { title: "年龄", render: (_, w: WorkloadSummary) => age(w.ageMs) },
                    {
                      title: "操作",
                      render: (_, w: WorkloadSummary) => w.kind === "Deployment" ? (
                        <Space>
                          <Button size="small" onClick={() => scale(w)}>扩缩容</Button>
                          <Button size="small" onClick={() => restart(w)}>重启</Button>
                          <Button size="small" onClick={() => checkRollout(w)}>进度</Button>
                          <Button size="small" danger onClick={() => removeApp(w)}>卸载</Button>
                        </Space>
                      ) : null,
                    },
                  ]} />
              ),
            },
            {
              key: "pods", label: "Pod",
              children: (
                <Table rowKey={(r) => `${r.namespace}/${r.name}`} size="small" pagination={{ pageSize: 10 }}
                  dataSource={pods}
                  columns={[
                    { title: "命名空间", dataIndex: "namespace" },
                    { title: "名称", dataIndex: "name" },
                    { title: "节点", dataIndex: "node" },
                    { title: "阶段", dataIndex: "phase", render: (s: string) => <Tag color={s === "Running" ? "green" : s === "Succeeded" ? "blue" : "orange"}>{s}</Tag> },
                    { title: "就绪", render: (_, p: PodSummary) => (p.ready ? "是" : "否") },
                    { title: "重启", dataIndex: "restarts", render: (n: number) => n >= 5 ? <Tag color="red">{n}</Tag> : n },
                    { title: "异常", dataIndex: "reason", render: (r: string | null) => r ? <Tag color="red">{r}</Tag> : "-" },
                    { title: "年龄", render: (_, p: PodSummary) => age(p.ageMs) },
                    { title: "操作", render: (_, p: PodSummary) => <Button size="small" onClick={() => viewLogs(p)}>日志</Button> },
                  ]} />
              ),
            },
            {
              key: "metrics", label: "资源用量",
              children: podMetrics === null ? (
                <Alert type="info" showIcon message="资源用量不可用" description="指标数据加载失败或集群不可达。" />
              ) : !podMetrics.available ? (
                <Alert type="info" showIcon message="资源用量不可用" description={podMetrics.note ?? "集群未安装 metrics-server。"} />
              ) : (
                <Table rowKey={(r) => `${r.namespace}/${r.name}`} size="small" pagination={{ pageSize: 10 }}
                  dataSource={podMetrics.pods}
                  columns={[
                    { title: "命名空间", dataIndex: "namespace" },
                    { title: "Pod", dataIndex: "name" },
                    { title: "CPU（毫核）", dataIndex: "cpu_milli", render: (v: number) => v.toLocaleString() },
                    { title: "内存（MiB）", dataIndex: "mem_mib", render: (v: number) => v.toLocaleString() },
                    {
                      title: "容器明细",
                      render: (_, r: PodMetricRow) => (
                        <Space size={4} wrap>
                          {r.containers.map((c) => (
                            <Tag key={c.name}>{c.name}: {c.cpu_milli}m / {c.mem_mib}Mi</Tag>
                          ))}
                        </Space>
                      ),
                    },
                  ]} />
              ),
            },
            {
              key: "events", label: "事件",
              children: (
                <Table rowKey={(r) => `${r.namespace}/${r.object}/${r.message}`} size="small" pagination={{ pageSize: 10 }}
                  dataSource={events}
                  columns={[
                    { title: "命名空间", dataIndex: "namespace" },
                    { title: "类型", dataIndex: "type", render: (t: string) => <Tag color={t === "Warning" ? "red" : "blue"}>{t}</Tag> },
                    { title: "原因", dataIndex: "reason" },
                    { title: "对象", dataIndex: "object" },
                    { title: "消息", dataIndex: "message", ellipsis: true },
                  ]} />
              ),
            },
          ]} />
          {rollout && (
            <Alert style={{ marginTop: 12 }} type={rollout.complete ? "success" : "info"} showIcon
              message={`滚动更新进度：${rollout.name}`}
              description={`期望 ${rollout.desired} / 已更新 ${rollout.updated} / 可用 ${rollout.available} — ${rollout.message}`} />
          )}
        </Card>
      )}

      <Modal
        title="接入集群"
        open={addOpen}
        onCancel={() => setAddOpen(false)}
        onOk={() => addForm.submit()}
        confirmLoading={busy}
        okText="连接测试并接入"
      >
        <Form form={addForm} layout="vertical" onFinish={addCluster}>
          <Form.Item name="name" label="集群名称" rules={[
            { required: true, message: "请输入集群名称" },
            { pattern: /^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$/, message: "小写字母数字与短横线，2-63 位" },
          ]}>
            <Input placeholder="prod-cluster" />
          </Form.Item>
          <Form.Item name="kubeconfig" label="kubeconfig 内容" rules={[{ required: true, message: "请粘贴 kubeconfig" }]}>
            <Input.TextArea rows={10} placeholder="apiVersion: v1&#10;kind: Config&#10;..." />
          </Form.Item>
          <Typography.Text type="secondary">
            kubeconfig 只保存在中控（落盘权限 600），不进审计与日志，绝不下发节点。
          </Typography.Text>
        </Form>
      </Modal>

      <Modal
        title="一键部署到集群"
        open={deployOpen}
        onCancel={() => setDeployOpen(false)}
        onOk={() => deployForm.submit()}
        confirmLoading={busy}
        okText="部署"
      >
        <Form form={deployForm} layout="vertical" initialValues={{ ns: "default", replicas: 1 }} onFinish={deploy}>
          <Form.Item name="template" label="模板" rules={[{ required: true, message: "请选择模板" }]}>
            <Select placeholder="选择应用模板"
              options={templates.map((t) => ({ value: t.id, label: `${t.name}（${t.arch}）` }))} />
          </Form.Item>
          <Form.Item name="app" label="应用名" rules={[
            { required: true, message: "请输入应用名" },
            { pattern: /^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$/, message: "小写字母数字与短横线，2-63 位" },
          ]}>
            <Input placeholder="my-blog" />
          </Form.Item>
          <Form.Item name="ns" label="命名空间" rules={[{ required: true }]}>
            <Input />
          </Form.Item>
          <Form.Item name="replicas" label="副本数">
            <InputNumber min={1} max={100} style={{ width: "100%" }} />
          </Form.Item>
          <Typography.Text type="secondary">
            模板渲染为 Deployment+Service 直发集群；数据卷以 PVC 挂载。与单机模板共用模板库。
          </Typography.Text>
        </Form>
      </Modal>

      <Modal
        title={`Pod 日志`}
        open={logText !== ""}
        onCancel={() => setLogText("")}
        footer={null}
        width={800}
      >
        <pre style={{ maxHeight: 480, overflow: "auto", fontSize: 12, whiteSpace: "pre-wrap" }}>{logText}</pre>
      </Modal>
    </Space>
  );
}
