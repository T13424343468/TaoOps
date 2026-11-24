import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Form, Input, InputNumber, Popconfirm, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A17 日志清理（真连 POST /api/nodes/{id}/logclean，契约 §12）。
// 规则按路径/大小/年龄匹配，命中文件先 gzip 归档（保留 7 天）再删；
// /etc、/usr、/bin 与数据目录为内置白名单，命中直接拒绝并告警。
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

interface CleanRule {
  id: string;
  path: string;
  max_size_mb: number;
  max_age_days: number;
  enabled: boolean;
  created_ms: number;
}

async function logcleanOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/logclean`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 30000, ...req }),
  });
}

function parseJson<T>(output: string): T[] {
  try {
    const v = JSON.parse(output);
    return Array.isArray(v) ? v : [];
  } catch {
    return [];
  }
}

export default function LogCleanPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [rules, setRules] = useState<CleanRule[]>([]);
  const [lastCleaned, setLastCleaned] = useState<{ path: string; size: number; archived: string }[]>([]);
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();

  const loadNodes = useCallback(async () => {
    const list = await api<NodeView[]>("/api/nodes");
    setNodes(list);
    setNodeId((cur) => cur || list[0]?.nodeId || "");
  }, []);

  const loadRules = useCallback(async (id: string) => {
    if (!id) return;
    try {
      const r = await logcleanOp(id, { op: "list" });
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      setRules(parseJson<CleanRule>(r.output));
    } catch (e) {
      msg.error(`加载规则失败：${(e as Error).message}`);
    }
  }, [msg]);

  useEffect(() => { void loadNodes(); }, [loadNodes]);
  useEffect(() => { void loadRules(nodeId); }, [nodeId, loadRules]);

  const run = async (req: Record<string, unknown>, ok: string) => {
    if (!nodeId) { msg.warning("先选择节点"); return; }
    setBusy(true);
    try {
      const r = await logcleanOp(nodeId, req);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      msg.success(ok);
      await loadRules(nodeId);
      return r;
    } catch (e) {
      msg.error(`操作失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const add = async (values: { path: string; maxSizeMb?: number; maxAgeDays?: number }) => {
    await run({
      op: "add", path: values.path,
      max_size_mb: values.maxSizeMb ?? 0, max_age_days: values.maxAgeDays ?? 0,
    }, "规则已添加（白名单目录会被拒绝）");
  };

  const clean = async (id?: string) => {
    const r = await run(id ? { op: "run", id } : { op: "run" }, "清理完成（命中文件已归档后删除）");
    if (r) setLastCleaned(parseJson(r.output));
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>日志清理</Typography.Title>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="按路径/大小/年龄规则清理日志：命中文件先 gzip 归档（保留 7 天）再删；/etc、/usr、/bin 与数据目录为内置白名单，命中直接拒绝并告警；每次清理留痕可查。" />
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ width: 260 }}
          value={nodeId || undefined}
          placeholder="节点"
          onChange={setNodeId}
          options={nodes.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
        <Button loading={busy} onClick={() => void loadRules(nodeId)}>刷新</Button>
        <Popconfirm title="对全部启用规则执行一轮清理？" onConfirm={() => void clean()}>
          <Button danger loading={busy}>立即清理</Button>
        </Popconfirm>
      </Space>

      <Card size="small" title="清理规则">
        <Table
          size="small"
          pagination={false}
          rowKey="id"
          dataSource={rules}
          locale={{ emptyText: "暂无规则，先在下方添加" }}
          columns={[
            { title: "路径（前缀匹配）", dataIndex: "path" },
            { title: "大小阈值", render: (_: unknown, r: CleanRule) => (r.max_size_mb > 0 ? `${r.max_size_mb} MB` : "不限") },
            { title: "年龄阈值", render: (_: unknown, r: CleanRule) => (r.max_age_days > 0 ? `${r.max_age_days} 天` : "不限") },
            { title: "状态", render: (_: unknown, r: CleanRule) => (r.enabled ? <Tag color="green">启用</Tag> : <Tag>停用</Tag>) },
            {
              title: "操作",
              render: (_: unknown, r: CleanRule) => (
                <Space>
                  <Button size="small" loading={busy} onClick={() => void clean(r.id)}>执行</Button>
                  <Popconfirm title="删除该规则？" onConfirm={() => void run({ op: "remove", id: r.id }, "规则已删除")}>
                    <Button size="small" danger loading={busy}>删除</Button>
                  </Popconfirm>
                </Space>
              ),
            },
          ]}
        />
      </Card>

      <Card size="small" title="添加规则" style={{ marginTop: 12 }}>
        <Form layout="inline" onFinish={add}>
          <Form.Item name="path" rules={[{ required: true, message: "路径必填（绝对路径）" }]}>
            <Input placeholder="日志目录（如 /var/log/app）" style={{ width: 260 }} />
          </Form.Item>
          <Form.Item name="maxSizeMb">
            <InputNumber min={0} placeholder="大小阈值 MB" style={{ width: 140 }} />
          </Form.Item>
          <Form.Item name="maxAgeDays">
            <InputNumber min={0} placeholder="年龄阈值（天）" style={{ width: 140 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>添加</Button>
          </Form.Item>
        </Form>
      </Card>

      {lastCleaned.length > 0 && (
        <Card size="small" title="最近一轮清理留痕" style={{ marginTop: 12 }}>
          <Table
            size="small"
            pagination={false}
            rowKey="path"
            dataSource={lastCleaned}
            columns={[
              { title: "文件", dataIndex: "path" },
              { title: "大小", render: (_: unknown, r: (typeof lastCleaned)[number]) => `${(r.size / 1024).toFixed(1)} KB` },
              { title: "归档位置", dataIndex: "archived" },
            ]}
          />
        </Card>
      )}
    </div>
  );
}
