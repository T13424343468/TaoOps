import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Empty, Select, Space, Spin, Typography, message } from "antd";
import { api } from "../api/client";

// AI 运维建议页（FR-A29，PRD v1.4）：真连 GET /api/advice/{id}（本地规则引擎）。
// 深度分析 POST /api/advice/{id}/analyze（外接模式，Operator+，需 Admin 配置端点）。
// 响应纯文本渲染（安全位评审：禁 HTML/MD 注入，URL 不自动转链）。

interface NodeView {
  nodeId: string;
  hostname: string;
  commandConnected: boolean;
}

interface Advice {
  id: string;
  level: string; // info | warn | critical
  title: string;
  basis: string;
  action_link: string | null;
  action_text: string | null;
}

export default function Ai() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [advices, setAdvices] = useState<Advice[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [analyzing, setAnalyzing] = useState(false);
  const [analysis, setAnalysis] = useState<string>("");

  useEffect(() => {
    api<NodeView[]>("/api/nodes").then((list) => {
      setNodes(list);
      const firstOnline = list.find((n) => n.commandConnected);
      if (firstOnline) setNodeId(firstOnline.nodeId);
    });
  }, []);

  const load = useCallback(async (id: string) => {
    if (!id) return;
    setLoading(true);
    setAnalysis("");
    try {
      const res = await api<{ advices: Advice[] }>(`/api/advice/${encodeURIComponent(id)}`);
      setAdvices(res.advices);
    } catch {
      message.error("加载建议失败");
      setAdvices([]);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (nodeId) void load(nodeId);
  }, [nodeId, load]);

  const analyze = async () => {
    if (!nodeId) return;
    setAnalyzing(true);
    try {
      const res = await api<{ advice: string }>(`/api/advice/${encodeURIComponent(nodeId)}/analyze`, {
        method: "POST",
      });
      setAnalysis(res.advice);
    } catch (e) {
      message.error(e instanceof Error ? e.message : "深度分析失败");
    } finally {
      setAnalyzing(false);
    }
  };

  const online = nodes.filter((n) => n.commandConnected);

  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Card
        title="AI 运维建议"
        extra={
          <Space>
            <Select
              style={{ width: 260 }}
              placeholder="选择节点"
              value={nodeId || undefined}
              onChange={setNodeId}
              options={online.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
            />
            <Button onClick={() => nodeId && load(nodeId)}>刷新</Button>
            <Button type="primary" loading={analyzing} onClick={analyze} disabled={!nodeId}>
              深度分析
            </Button>
          </Space>
        }
      >
        {loading ? (
          <Spin />
        ) : advices && advices.length > 0 ? (
          <Space direction="vertical" style={{ width: "100%" }} size="small">
            {advices.map((a) => (
              <Alert
                key={a.id}
                type={a.level === "critical" ? "error" : a.level === "warn" ? "warning" : "info"}
                message={a.title}
                description={
                  <>
                    <Typography.Text>{a.basis}</Typography.Text>
                    {a.action_link && a.action_text && (
                      <div>
                        <a href={`#${a.action_link}`}>{a.action_text}</a>
                      </div>
                    )}
                  </>
                }
              />
            ))}
          </Space>
        ) : (
          <Empty description={advices ? "暂无建议：各项指标正常" : "选择节点查看建议"} />
        )}
      </Card>

      {analysis && (
        <Card title="深度分析（外接模式）">
          {/* 纯文本渲染：外接响应不可信，禁 HTML 注入（安全位评审口径）。 */}
          <Typography.Paragraph style={{ whiteSpace: "pre-wrap", marginBottom: 0 }}>
            {analysis}
          </Typography.Paragraph>
        </Card>
      )}

      <Card size="small">
        <Typography.Text type="secondary">
          本地建议由规则引擎基于监控、告警、证书、备份与容器数据生成；深度分析需管理员在系统设置中配置外接端点后可用。
        </Typography.Text>
      </Card>
    </Space>
  );
}
