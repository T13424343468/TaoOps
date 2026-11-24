import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Drawer, Input, Popconfirm, Select, Space, Table, Tag, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A25 在线文件编辑器（真连）：目录浏览走 files.manage op=list（通用 cmd 通道），
// 文本编辑走专用端点 /api/nodes/{id}/files/edit（file_read/file_write，均限 Operator+）。
// Agent 侧约束：≤1MB、二进制（NUL 字节）拒绝、写前自动 .bak.<ts> 备份保留 5 份。
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

interface Entry {
  name: string;
  is_dir: boolean;
  size: number;
  mode: string;
}

async function cmd(nodeId: string, args: string[]): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/cmd`, {
    method: "POST",
    body: JSON.stringify({ command: "files.manage", args, timeoutMs: 30000 }),
  });
}

async function fileEdit(nodeId: string, op: "file_read" | "file_write", path: string, content?: string): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/files/edit`, {
    method: "POST",
    body: JSON.stringify({ op, path, content, timeoutMs: 30000 }),
  });
}

function parseEntries(output: string): Entry[] {
  try {
    const v = JSON.parse(output);
    return Array.isArray(v?.entries) ? v.entries : [];
  } catch {
    return [];
  }
}

function fmtSize(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

export default function FilesPage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [dir, setDir] = useState<string>("/");
  const [entries, setEntries] = useState<Entry[]>([]);
  const [busy, setBusy] = useState(false);
  const [editPath, setEditPath] = useState<string | null>(null);
  const [editContent, setEditContent] = useState("");
  const [editLoading, setEditLoading] = useState(false);
  const [msg, contextHolder] = message.useMessage();

  const loadNodes = useCallback(async () => {
    const list = await api<NodeView[]>("/api/nodes");
    setNodes(list);
    setNodeId((cur) => cur || list[0]?.nodeId || "");
  }, []);

  const loadDir = useCallback(async (id: string, d: string) => {
    if (!id) return;
    try {
      const r = await cmd(id, [`op=list`, `dir=${d}`]);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      setEntries(parseEntries(r.output));
    } catch (e) {
      msg.error(`加载目录失败：${(e as Error).message}`);
    }
  }, [msg]);

  useEffect(() => { void loadNodes(); }, [loadNodes]);
  useEffect(() => { void loadDir(nodeId, dir); }, [nodeId, dir, loadDir]);

  const openEditor = async (name: string) => {
    const p = dir === "/" ? `/${name}` : `${dir}/${name}`;
    setEditPath(p);
    setEditContent("");
    setEditLoading(true);
    try {
      const r = await fileEdit(nodeId, "file_read", p);
      if (r.exit_code !== 0) { msg.error(r.output); setEditPath(null); return; }
      setEditContent(r.output);
    } catch (e) {
      msg.error(`读取失败：${(e as Error).message}`);
      setEditPath(null);
    } finally {
      setEditLoading(false);
    }
  };

  const save = async () => {
    if (!editPath) return;
    setBusy(true);
    try {
      const r = await fileEdit(nodeId, "file_write", editPath, editContent);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      msg.success("已保存（原文件已自动备份）");
      setEditPath(null);
      await loadDir(nodeId, dir);
    } catch (e) {
      msg.error(`保存失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>文件管理</Typography.Title>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="在线编辑小文本文件（≤1MB，二进制文件拒绝）：保存前自动在同目录生成 .bak.<时间戳> 备份，保留最近 5 份；读写均限 Operator 及以上并留审计。" />
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ width: 260 }}
          value={nodeId || undefined}
          placeholder="节点"
          onChange={setNodeId}
          options={nodes.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
        <Input
          style={{ width: 420 }}
          value={dir}
          onChange={(e) => setDir(e.target.value)}
          placeholder="目录（绝对路径）"
          onPressEnter={() => void loadDir(nodeId, dir)}
        />
        <Button loading={busy} onClick={() => void loadDir(nodeId, dir)}>刷新</Button>
      </Space>

      <Card size="small" title={`目录：${dir}`}>
        <Table
          size="small"
          pagination={false}
          rowKey="name"
          dataSource={entries}
          locale={{ emptyText: "目录为空或加载失败" }}
          columns={[
            {
              title: "名称",
              dataIndex: "name",
              render: (_: unknown, e: Entry) =>
                e.is_dir ? (
                  <a onClick={() => setDir(dir === "/" ? `/${e.name}` : `${dir}/${e.name}`)}>{e.name}/</a>
                ) : (
                  <a onClick={() => void openEditor(e.name)}>{e.name}</a>
                ),
            },
            { title: "大小", render: (_: unknown, e: Entry) => (e.is_dir ? "—" : fmtSize(e.size)) },
            { title: "权限", dataIndex: "mode", render: (m: string) => <Tag>{m}</Tag> },
          ]}
        />
      </Card>

      <Drawer
        title={`编辑：${editPath ?? ""}`}
        open={editPath !== null}
        onClose={() => setEditPath(null)}
        width={640}
        destroyOnClose
      >
        <Input.TextArea
          rows={22}
          value={editContent}
          onChange={(e) => setEditContent(e.target.value)}
          placeholder={editLoading ? "加载中…" : "文件内容"}
        />
        <Space style={{ marginTop: 12 }}>
          <Popconfirm title="确认保存？原文件会自动备份" onConfirm={() => void save()}>
            <Button type="primary" loading={busy} disabled={editLoading}>保存</Button>
          </Popconfirm>
          <Button onClick={() => setEditPath(null)}>取消</Button>
        </Space>
      </Drawer>
    </div>
  );
}
