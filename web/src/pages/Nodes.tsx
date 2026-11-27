import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// 多机批量下发页（FR-A30，PRD v1.4）：真连 POST /api/batch。
// 白名单逐 op 枚举纯只读（后端硬编码）；并发 ≤5；限频 6 批/分钟/用户；
// 节点归属服务端二次校验（离线/不存在拒绝整批）；聚合详情单节点截断 64KB。

interface NodeView {
  nodeId: string;
  hostname: string;
  commandConnected: boolean;
}

interface BatchNodeResult {
  node_id: string;
  status: string; // success | timeout | error
  exit_code: number;
  output: string;
  request_id: string;
}

interface BatchResult {
  batch_id: string;
  module: string;
  success_count: number;
  failure_count: number;
  nodes: BatchNodeResult[];
}

// 操作选项与后端白名单逐 op 对应（PRD §五：纯只读）。
const OPS = [
  { value: "sysinfo.overview", label: "系统概览" },
  { value: "sysinfo.metrics_sample", label: "资源采样" },
  { value: "sysinfo.process_top", label: "进程 Top10" },
  { value: "cron.manage:list", label: "计划任务清单" },
  { value: "docker.manage:list", label: "容器清单" },
  { value: "docker.manage:image_list", label: "镜像清单" },
  { value: "logclean.manage:list", label: "日志清理清单" },
  { value: "logclean.manage:preview", label: "日志清理预览" },
  { value: "ssl.manage:list", label: "证书清单" },
];

export default function Nodes() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [selected, setSelected] = useState<string[]>([]);
  const [op, setOp] = useState<string>("sysinfo.overview");
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<BatchResult | null>(null);

  useEffect(() => {
    api<NodeView[]>("/api/nodes").then(setNodes);
  }, []);

  const online = nodes.filter((n) => n.commandConnected);

  const run = useCallback(async () => {
    if (selected.length === 0 || !op) return;
    setRunning(true);
    setResult(null);
    const [module, opName] = op.split(":");
    try {
      const res = await api<BatchResult>("/api/batch", {
        method: "POST",
        body: JSON.stringify({
          module,
          args: opName ? [opName] : [],
          node_ids: selected,
        }),
      });
      setResult(res);
      if (res.failure_count > 0) message.warning(`${res.success_count} 台成功，${res.failure_count} 台失败`);
    } catch (e) {
      message.error(e instanceof Error ? e.message : "批量下发失败");
    } finally {
      setRunning(false);
    }
  }, [selected, op]);

  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Card
        title="多机批量下发"
        extra={
          <Space>
            <Select
              style={{ width: 200 }}
              value={op}
              onChange={setOp}
              options={OPS.map((o) => ({ value: o.value, label: o.label }))}
            />
            <Button type="primary" loading={running} onClick={run} disabled={selected.length === 0}>
              执行（{selected.length} 台）
            </Button>
          </Space>
        }
      >
        <Alert
          type="info"
          showIcon
          message="仅支持只读操作的批量执行；一次最多 20 台，每分钟最多 6 批。"
          style={{ marginBottom: 12 }}
        />
        <Select
          mode="multiple"
          style={{ width: "100%" }}
          placeholder="选择在线节点（离线节点不可选）"
          value={selected}
          onChange={setSelected}
          options={online.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
      </Card>

      {result && (
        <Card title={`执行结果 ${result.batch_id.slice(0, 8)}：成功 ${result.success_count} / 失败 ${result.failure_count}`}>
          <Table<BatchNodeResult>
            rowKey="node_id"
            size="small"
            pagination={{ pageSize: 10 }}
            dataSource={result.nodes}
            columns={[
              {
                title: "节点",
                dataIndex: "node_id",
                render: (id: string) => {
                  const n = nodes.find((x) => x.nodeId === id);
                  return n ? `${n.hostname} (${id.slice(0, 8)})` : id.slice(0, 8);
                },
              },
              {
                title: "状态",
                dataIndex: "status",
                width: 100,
                render: (s: string) => (
                  <Tag color={s === "success" ? "green" : s === "timeout" ? "orange" : "red"}>{s}</Tag>
                ),
              },
              { title: "退出码", dataIndex: "exit_code", width: 80 },
              {
                title: "输出",
                dataIndex: "output",
                ellipsis: true,
                render: (o: string) => (
                  <Typography.Text code style={{ fontSize: 12 }}>
                    {o.length > 200 ? `${o.slice(0, 200)}…` : o}
                  </Typography.Text>
                ),
              },
            ]}
          />
        </Card>
      )}
    </Space>
  );
}
