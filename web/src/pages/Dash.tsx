import { useState } from "react";
import { Card, Col, Row, Statistic, Table, Typography } from "antd";
import { mockMetrics, mockNodes, type NodeInfo } from "../api/mock";

export default function DashPage() {
  const [nodes] = useState<NodeInfo[]>(() => mockNodes());
  const [metrics] = useState(() => mockMetrics());
  const online = nodes.filter((n) => n.status === "online").length;

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
            { title: "ID", dataIndex: "nodeId" },
            { title: "系统", dataIndex: "os" },
            { title: "状态", dataIndex: "status", render: (s: string) => s === "online" ? "在线" : "离线" },
            { title: "CPU%", dataIndex: "cpu" },
            { title: "内存%", dataIndex: "mem" },
            { title: "磁盘%", dataIndex: "disk" },
            { title: "负载", dataIndex: "load" },
          ]}
        />
      </Card>
    </div>
  );
}
