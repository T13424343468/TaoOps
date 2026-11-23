import { useState } from "react";
import { Alert, Button, Card, Space, Table, Typography, message } from "antd";
import { mockDispatch, mockNodes } from "../api/mock";

// Generic module page for command-backed resources. All data is mock-backed
// (HTTP->gRPC dispatch bridge pending, see breakpoint-web.md mock manifest);
// actions run through the same mock dispatcher so the interaction path is real.
export interface ModuleColumn {
  key: string;
  title: string;
}

export interface ModulePageConfig {
  title: string;
  description: string;
  columns: ModuleColumn[];
  rows: Record<string, string | number>[];
  commands: { label: string; command: string; args?: string[] }[];
}

export default function ModulePage({ config }: { config: ModulePageConfig }) {
  const [selected, setSelected] = useState<Record<string, string | number> | null>(null);
  const [nodeId, setNodeId] = useState(mockNodes()[0]?.nodeId ?? "node-01");
  const [output, setOutput] = useState<string>("");
  const [msg, contextHolder] = message.useMessage();

  const run = async (command: string, args?: string[]) => {
    if (!selected) { msg.warning("先在表格中选择一行资源"); return; }
    const r = mockDispatch(nodeId, `${command} ${args?.join(" ") ?? ""} ${JSON.stringify(selected)}`.trim());
    setOutput(`$ ${command}\n${r.output}`);
    if (!r.ok) msg.error(r.output);
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>{config.title}</Typography.Title>
      <Alert type="warning" showIcon style={{ marginBottom: 12 }}
        message="本页数据为 mock（后端 HTTP→gRPC 下发桥接未就绪，见 mock 清单）；页面结构、交互与对账口径为最终形态。" />
      <Card size="small" title="资源">
        <Table
          rowKey={(r: Record<string, string | number>) => JSON.stringify(r).slice(0, 64)}
          size="small"
          pagination={false}
          rowSelection={{ type: "radio", onChange: (_: unknown, rows: Record<string, string | number>[]) => setSelected(rows[0] ?? null) }}
          dataSource={config.rows}
          columns={config.columns.map((c) => ({ title: c.title, dataIndex: c.key }))}
        />
      </Card>
      <Card size="small" title="操作" style={{ marginTop: 12 }}>
        <Space wrap style={{ marginBottom: 8 }}>
          <select value={nodeId} onChange={(e) => setNodeId(e.target.value)} style={{ padding: 4 }}>
            {mockNodes().map((n) => <option key={n.nodeId} value={n.nodeId}>{n.hostname}</option>)}
          </select>
          {config.commands.map((c) => (
            <Button key={c.command} size="small" onClick={() => run(c.command, c.args)}>{c.label}</Button>
          ))}
        </Space>
        {output && <pre style={{ background: "#000", color: "#0f0", padding: 8, borderRadius: 4, maxHeight: 200, overflow: "auto", fontSize: 12 }}>{output}</pre>}
      </Card>
    </div>
  );
}
