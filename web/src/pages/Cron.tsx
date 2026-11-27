import { useCallback, useEffect, useState } from "react";
import { Button, Card, Drawer, Form, Input, Popconfirm, Select, Space, Table, Typography, message } from "antd";
import { api } from "../api/client";

// 计划任务页（真连 POST /api/nodes/{id}/cron，backlog B2 切真）。
// 任务列表/新增/删除/立即执行/执行日志，走 cron.manage 通道；list/logs 只读，add/remove/run 限 Operator+。

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

interface CronJob {
  name: string;
  schedule: string;
  command: string;
}

interface LogEntry {
  name: string;
  path: string;
  size: number;
  mod_time: string;
}

async function cronOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/cron`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 60000, ...req }),
  });
}

// list 的结构化结果按模块约定落 Output JSON（jobs 数组）。
function parseJobs(output: string): CronJob[] {
  try {
    const parsed = JSON.parse(output);
    return Array.isArray(parsed?.jobs) ? parsed.jobs : [];
  } catch {
    return [];
  }
}

function parseLogs(output: string): LogEntry[] {
  try {
    const parsed = JSON.parse(output);
    return Array.isArray(parsed?.logs) ? parsed.logs : [];
  } catch {
    return [];
  }
}

export default function Cron() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [nodeId, setNodeId] = useState<string>("");
  const [jobs, setJobs] = useState<CronJob[]>([]);
  const [loading, setLoading] = useState(false);
  const [addOpen, setAddOpen] = useState(false);
  const [form] = Form.useForm();
  const [logTarget, setLogTarget] = useState<string>("");
  const [logs, setLogs] = useState<LogEntry[]>([]);

  useEffect(() => {
    api<NodeView[]>("/api/nodes").then((list) => {
      setNodes(list);
      const first = list.find((n) => n.commandConnected) ?? list[0];
      if (first) setNodeId(first.nodeId);
    }).catch(() => message.error("节点列表加载失败"));
  }, []);

  const refresh = useCallback(async () => {
    if (!nodeId) return;
    setLoading(true);
    try {
      const res = await cronOp(nodeId, { op: "list" });
      setJobs(parseJobs(res.output));
    } catch {
      message.error("任务列表加载失败");
    } finally {
      setLoading(false);
    }
  }, [nodeId]);

  useEffect(() => { refresh(); }, [refresh]);

  const onAdd = async () => {
    const values = await form.validateFields();
    try {
      await cronOp(nodeId, { op: "add", name: values.name, schedule: values.schedule, command: values.command });
      message.success("任务已添加");
      setAddOpen(false);
      form.resetFields();
      refresh();
    } catch (e) {
      message.error(`添加失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onRemove = async (name: string) => {
    try {
      await cronOp(nodeId, { op: "remove", name });
      message.success("任务已删除");
      refresh();
    } catch (e) {
      message.error(`删除失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onRun = async (job: CronJob) => {
    try {
      await cronOp(nodeId, { op: "run", name: job.name, command: job.command });
      message.success("已触发执行");
    } catch (e) {
      message.error(`执行失败：${e instanceof Error ? e.message : "未知错误"}`);
    }
  };

  const onLogs = async (name: string) => {
    setLogTarget(name);
    setLogs([]);
    try {
      const res = await cronOp(nodeId, { op: "logs", name });
      setLogs(parseLogs(res.output));
    } catch {
      message.error("日志加载失败");
    }
  };

  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Card
        title="计划任务"
        extra={
          <Space>
            <Select
              style={{ minWidth: 220 }}
              value={nodeId || undefined}
              onChange={setNodeId}
              options={nodes.map((n) => ({
                value: n.nodeId,
                label: `${n.hostname}${n.commandConnected ? "" : "（离线）"}`,
              }))}
              placeholder="选择节点"
            />
            <Button type="primary" onClick={() => setAddOpen(true)}>新增任务</Button>
            <Button onClick={refresh}>刷新</Button>
          </Space>
        }
      >
        <Table<CronJob>
          rowKey="name"
          loading={loading}
          dataSource={jobs}
          pagination={false}
          columns={[
            { title: "任务名", dataIndex: "name" },
            { title: "周期", dataIndex: "schedule" },
            { title: "命令", dataIndex: "command", ellipsis: true },
            {
              title: "操作",
              render: (_, job) => (
                <Space>
                  <Button size="small" onClick={() => onRun(job)}>立即执行</Button>
                  <Button size="small" onClick={() => onLogs(job.name)}>日志</Button>
                  <Popconfirm title={`确认删除任务 ${job.name}？`} onConfirm={() => onRemove(job.name)}>
                    <Button size="small" danger>删除</Button>
                  </Popconfirm>
                </Space>
              ),
            },
          ]}
        />
      </Card>

      <Drawer
        title={`执行日志 · ${logTarget}`}
        open={logTarget !== ""}
        onClose={() => setLogTarget("")}
        width={520}
      >
        {logs.length === 0 ? (
          <Typography.Text type="secondary">暂无日志文件</Typography.Text>
        ) : (
          <Table<LogEntry>
            rowKey="path"
            dataSource={logs}
            pagination={false}
            columns={[
              { title: "文件", dataIndex: "path", ellipsis: true },
              { title: "大小", dataIndex: "size", width: 90 },
              { title: "修改时间", dataIndex: "mod_time", width: 170 },
            ]}
          />
        )}
      </Drawer>

      {addOpen && (
        <Card title="新增计划任务" style={{ marginTop: 16 }}>
          <Form form={form} layout="vertical" style={{ maxWidth: 560 }}>
            <Form.Item name="name" label="任务名" rules={[{ required: true, message: "请输入任务名" }]}>
              <Input placeholder="如 nightly-backup" />
            </Form.Item>
            <Form.Item name="schedule" label="周期（cron 表达式）" rules={[{ required: true, message: "请输入周期" }]}>
              <Input placeholder="如 0 2 * * *" />
            </Form.Item>
            <Form.Item name="command" label="命令" rules={[{ required: true, message: "请输入命令" }]}>
              <Input placeholder="如 /usr/local/bin/backup.sh --full" />
            </Form.Item>
            <Space>
              <Button type="primary" onClick={onAdd}>确认添加</Button>
              <Button onClick={() => setAddOpen(false)}>取消</Button>
            </Space>
          </Form>
        </Card>
      )}
    </Space>
  );
}
