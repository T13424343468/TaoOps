import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Descriptions, Form, Input, Select, Space, Steps, Typography, message } from "antd";
import { api } from "../api/client";

// FR-A14 一键迁移：precheck（预检报告）→ pack（打包 + 迁移清单）→
// backup.manage op=send（经 §7 中转，轮询 send_status）→ 目标端 restore
// （confirm=true + 自动快照 + 版本比对 + 路径映射校验）。
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

async function cmd(nodeId: string, command: string, args: string[]): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/cmd`, {
    method: "POST",
    body: JSON.stringify({ command, args, timeout_ms: 60000 }),
  });
}

interface PrecheckReport {
  source_path: string;
  target_path: string;
  files: number;
  total_size: number;
  source_version: string;
  checks: { name: string; status: string; detail?: string }[];
  forced?: boolean;
}

export default function MigratePage() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [sourceNode, setSourceNode] = useState<string>("");
  const [targetNode, setTargetNode] = useState<string>("");
  const [step, setStep] = useState(0);
  const [report, setReport] = useState<PrecheckReport | null>(null);
  const [packedFile, setPackedFile] = useState<string>("");
  const [packedManifest, setPackedManifest] = useState<string>("");
  const [sendStatus, setSendStatus] = useState<string>("");
  const [restoreMsg, setRestoreMsg] = useState<string>("");
  const [busy, setBusy] = useState(false);
  const [msg, contextHolder] = message.useMessage();

  const loadNodes = useCallback(async () => {
    const list = await api<NodeView[]>("/api/nodes");
    setNodes(list);
    setSourceNode((cur) => cur || list[0]?.nodeId || "");
  }, []);

  useEffect(() => { void loadNodes(); }, [loadNodes]);

  const precheck = async (values: { sourcePath: string; targetPath: string; force?: boolean }) => {
    if (!sourceNode) { msg.warning("先选择源节点"); return; }
    setBusy(true);
    try {
      const args = ["op=precheck", `source_path=${values.sourcePath}`, `target_path=${values.targetPath}`];
      if (values.force) args.push("force=true");
      const r = await cmd(sourceNode, "migrate.manage", args);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      // 报告随 data.report 返回；桥接层只回 output 时退化为文本展示。
      setReport({ source_path: values.sourcePath, target_path: values.targetPath, files: -1, total_size: -1, source_version: "", checks: [], forced: values.force });
      msg.success("预检通过，可开始打包");
      setStep(1);
    } catch (e) {
      msg.error(`预检失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const pack = async (values: { sourcePath: string; targetPath: string; name: string }) => {
    setBusy(true);
    try {
      const r = await cmd(sourceNode, "migrate.manage", [
        "op=pack",
        `source_path=${values.sourcePath}`,
        `target_path=${values.targetPath}`,
        `name=${values.name}`,
      ]);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      setPackedFile(r.output.match(/ready: (\S+)/)?.[1] ?? "");
      setPackedManifest(r.output);
      msg.success("打包完成，开始中转传输");
      setStep(2);
      void sendToTarget(values.name, r.output.match(/ready: (\S+)/)?.[1] ?? "");
    } catch (e) {
      msg.error(`打包失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  const sendToTarget = async (name: string, file: string) => {
    try {
      const r = await cmd(sourceNode, "backup.manage", [
        "op=send", `file=${file}`, `target_node=${targetNode}`, `name=${name}`,
      ]);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      for (let i = 0; i < 120; i++) {
        await new Promise((res) => setTimeout(res, 2000));
        const s = await cmd(sourceNode, "backup.manage", ["op=send_status"]);
        setSendStatus(s.output);
        if (s.output.includes("status=done")) { msg.success("中转完成，目标端已整包校验"); setStep(3); break; }
        if (s.output.includes("status=failed")) { msg.error(`中转失败：${s.output}`); break; }
      }
    } catch (e) {
      msg.error(`中转失败：${(e as Error).message}`);
    }
  };

  const restore = async (values: { targetPath: string; sourceVersion?: string; force?: boolean }) => {
    setBusy(true);
    try {
      const mapping = JSON.stringify({ target_path: values.targetPath });
      const args = [
        "op=restore",
        `file=${packedFile}`,
        `mapping=${mapping}`,
        "confirm=true",
      ];
      if (values.sourceVersion) args.push(`source_version=${values.sourceVersion}`);
      if (values.force) args.push("force=true");
      const r = await cmd(targetNode, "migrate.manage", args);
      setRestoreMsg(r.output);
      if (r.exit_code !== 0) { msg.error(r.output); return; }
      msg.success("迁移完成，目标端已还原并保留回滚快照");
      setStep(4);
    } catch (e) {
      msg.error(`还原失败：${(e as Error).message}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div>
      {contextHolder}
      <Typography.Title level={4}>一键迁移</Typography.Title>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="站点从源节点打包经中控中转到目标节点还原；还原需 confirm=true，还原前自动快照；环境版本不一致默认拒绝，可 force 跳过并留审计。" />
      <Steps
        size="small"
        current={step}
        items={[{ title: "预检" }, { title: "打包" }, { title: "中转" }, { title: "还原" }, { title: "完成" }]}
        style={{ marginBottom: 16 }}
      />
      <Space style={{ marginBottom: 12 }}>
        <Select
          style={{ width: 260 }}
          value={sourceNode || undefined}
          placeholder="源节点"
          onChange={setSourceNode}
          options={nodes.map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
        <Select
          style={{ width: 260 }}
          value={targetNode || undefined}
          placeholder="目标节点"
          onChange={setTargetNode}
          options={nodes.filter((n) => n.nodeId !== sourceNode).map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId.slice(0, 8)})` }))}
        />
      </Space>

      <Card size="small" title="1. 预检（源节点）">
        <Form layout="inline" onFinish={precheck}>
          <Form.Item name="sourcePath" rules={[{ required: true, message: "源路径必填" }]}>
            <Input placeholder="源站点绝对路径（如 /var/www/site）" style={{ width: 300 }} />
          </Form.Item>
          <Form.Item name="targetPath" rules={[{ required: true, message: "目标路径必填" }]}>
            <Input placeholder="目标绝对路径（如 /var/www/site）" style={{ width: 300 }} />
          </Form.Item>
          <Form.Item name="force" valuePropName="checked">
            <Select style={{ width: 140 }} allowClear placeholder="force"
              options={[{ value: true, label: "force 跳过校验" }]} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy}>开始预检</Button>
          </Form.Item>
        </Form>
        {report && (
          <Descriptions size="small" column={2} style={{ marginTop: 12 }}>
            <Descriptions.Item label="源路径">{report.source_path}</Descriptions.Item>
            <Descriptions.Item label="目标路径">{report.target_path}</Descriptions.Item>
          </Descriptions>
        )}
      </Card>

      <Card size="small" title="2. 打包（源节点）" style={{ marginTop: 12 }}>
        <Form layout="inline" onFinish={pack}>
          <Form.Item name="sourcePath" rules={[{ required: true, message: "源路径必填" }]}>
            <Input placeholder="源站点绝对路径" style={{ width: 300 }} />
          </Form.Item>
          <Form.Item name="targetPath" rules={[{ required: true, message: "目标路径必填" }]}>
            <Input placeholder="目标绝对路径" style={{ width: 300 }} />
          </Form.Item>
          <Form.Item name="name" rules={[{ required: true, message: "迁移名必填" }]}>
            <Input placeholder="迁移名（如 site-a）" style={{ width: 160 }} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy} disabled={step < 1}>打包并发送</Button>
          </Form.Item>
        </Form>
        {packedManifest && (
          <Typography.Paragraph code style={{ marginTop: 12, marginBottom: 0 }}>{packedManifest}</Typography.Paragraph>
        )}
      </Card>

      <Card size="small" title="3. 中转状态" style={{ marginTop: 12 }}>
        {sendStatus ? (
          <Typography.Paragraph code style={{ marginBottom: 0 }}>{sendStatus}</Typography.Paragraph>
        ) : (
          <Typography.Text type="secondary">打包后自动开始中转</Typography.Text>
        )}
      </Card>

      <Card size="small" title="4. 目标端还原（confirm）" style={{ marginTop: 12 }}>
        <Form layout="inline" onFinish={restore}>
          <Form.Item name="targetPath" rules={[{ required: true, message: "目标路径必填" }]}>
            <Input placeholder="目标绝对路径" style={{ width: 300 }} />
          </Form.Item>
          <Form.Item name="sourceVersion">
            <Input placeholder="源端版本（可选）" style={{ width: 160 }} />
          </Form.Item>
          <Form.Item name="force" valuePropName="checked">
            <Select style={{ width: 140 }} allowClear placeholder="force"
              options={[{ value: true, label: "force 跳过版本比对" }]} />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={busy} disabled={step < 3}>确认还原</Button>
          </Form.Item>
        </Form>
        {restoreMsg && (
          <Typography.Paragraph code style={{ marginTop: 12, marginBottom: 0 }}>{restoreMsg}</Typography.Paragraph>
        )}
      </Card>
    </div>
  );
}
