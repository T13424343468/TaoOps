import { useCallback, useEffect, useState } from "react";
import { Card, Col, Row, Statistic, Table, Tag, Typography } from "antd";
import { api } from "../api/client";
import { mockMetrics } from "../api/mock";

// FR-A22：节点健康表真连 /api/nodes，展示连接状态与最近在线时间；
// 断连告警由面板后台巡检（NodeOfflineMonitor），此处仅展示。
interface NodeView {
  nodeId: string;
  hostname: string;
  lastSeenMs: number;
  registeredAtMs: number;
  commandConnected: boolean;
}

const fmtTime = (ms: number) => (ms > 0 ? new Date(ms).toLocaleString() : "—");

export default function DashPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [metrics] = useState(() => mockMetrics());

  const loadNodes = useCallback(async () => {
    try { setNodes(await api<NodeView[]>("/api/nodes")); }
    catch { setNodes([]); }
  }, []);

  useEffect(() => { void loadNodes(); }, [loadNodes]);

  const online = nodes.filter((n) => n.commandConnected).length;

  return (
    <div>
      <Typography.Title level={4}>总览</Typography.Title>
      <Row gutter={16}>
        <Col span={6}><Card><Statistic title="节点总数" value={nodes.length} /></Card></Col>
        <Col span={6}><Card><Statistic title="在线" value={online} valueStyle={{ color: "#3f8600" }} /></Card></Col>
        <Col span={6}><Card><Statistic title="离线" value={nodes.length - online} valueStyle={{ color: "#cf1322" }} /></Card></Col>
        <Col span={6}><Card><Statistic title="24h 峰值 CPU%" value={Math.max(...metrics.map((m) => m.cpu))} /></Card></Col>
      </Row>
      <Card title="节点健康" style={{ marginTop: 16 }} size="small">
        <Table
          rowKey="nodeId"
          size="small"
          pagination={false}
          dataSource={nodes}
          columns={[
            { title: "节点", dataIndex: "hostname" },
            { title: "ID", dataIndex: "nodeId", render: (v: string) => v.slice(0, 8) },
            {
              title: "连接状态", dataIndex: "commandConnected",
              render: (ok: boolean) => ok ? <Tag color="green">在线</Tag> : <Tag color="red">离线</Tag>,
            },
            { title: "最近在线", dataIndex: "lastSeenMs", render: fmtTime },
            { title: "注册时间", dataIndex: "registeredAtMs", render: fmtTime },
          ]}
        />
      </Card>
    </div>
  );
}
