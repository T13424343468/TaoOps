import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Checkbox, Descriptions, Form, Input, Popconfirm, Select, Space, Steps, Typography, message } from "antd";
import { api } from "../api/client";

// 升级页（真连 POST /api/nodes/{id}/migrate，backlog B2 切真）。
// 离线升级三步：precheck（预检）→ pack（打包）→ restore（目标端还原，confirm 门）。
// 走 migrate.manage 通道；precheck/pack 限 Operator+，restore 限 Admin（MigratePolicy）。

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

async function migrateOp(nodeId: string, req: Record<string, unknown>): Promise<CmdOutcome> {
  return api<CmdOutcome>(`/api/nodes/${encodeURIComponent(nodeId)}/migrate`, {
    method: "POST",
    body: JSON.stringify({ timeoutMs: 600000, ...req }),
  });
}

export default function Upgrade() {
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [sourceNode, setSourceNode] = useState<string>("");
  const [targetNode, setTargetNode] = useState<string>("");
  const [step, setStep] = useState(0);
  const [busy, setBusy] = useState(false);
  const [packedFile, setPackedFile] = useState("");
  const [precheckMsg, setPrecheckMsg] = useState("");
  const [restoreMsg, setRestoreMsg] = useState("");
  const [preForm] = Form.useForm();
  const [restoreForm] = Form.useForm();

  useEffect(() => {
    api<NodeView[]>("/api/nodes").then((list) => {
      setNodes(list);
      const first = list.find((n) => n.commandConnected) ?? list[0];
      if (first) setSourceNode(first.nodeId);
    }).catch(() => message.error("节点列表加载失败"));
  }, []);

  const precheck = useCallback(async () => {
    if (!sourceNode) { message.warning("先选择源节点"); return; }
    setBusy(true);
    try {
      const values = await preForm.validateFields();
      const res = await migrateOp(sourceNode, {
        op: "precheck",
        source_path: values.sourcePath,
        target_path: values.targetPath,
        force: values.force ?? false,
      });
      if (res.exit_code !== 0) {
        message.error(`预检失败：${res.output}`);
        return;
      }
      setPrecheckMsg(res.output);
      message.success("预检通过，可开始打包");
      setStep(1);
    } catch (e) {
      message.error(`预检失败：${e instanceof Error ? e.message : "未知错误"}`);
    } finally {
      setBusy(false);
    }
  }, [sourceNode, preForm]);

  const pack = useCallback(async () => {
    setBusy(true);
    try {
      const values = await preForm.validateFields();
      const res = await migrateOp(sourceNode, {
        op: "pack",
        source_path: values.sourcePath,
        target_path: values.targetPath,
        name: values.name,
      });
      if (res.exit_code !== 0) {
        message.error(`打包失败：${res.output}`);
        return;
      }
      // pack 输出 "migration package ready: <archive> (N files, M bytes)"。
      const file = res.output.match(/ready: (\S+)/)?.[1] ?? "";
      setPackedFile(file);
      message.success("打包完成，可在目标端还原");
      setStep(2);
    } catch (e) {
      message.error(`打包失败：${e instanceof Error ? e.message : "未知错误"}`);
    } finally {
      setBusy(false);
    }
  }, [sourceNode, preForm]);

  const restore = async () => {
    setBusy(true);
    try {
      const values = await restoreForm.validateFields();
      const mapping = JSON.stringify({ target_path: values.targetPath });
      const res = await migrateOp(targetNode, {
        op: "restore",
        file: packedFile,
        mapping,
        confirm: true,
        force: values.force ?? false,
        source_version: values.sourceVersion || undefined,
      });
      setRestoreMsg(res.output);
      if (res.exit_code !== 0) {
        message.error(`还原失败：${res.output}`);
        return;
      }
      message.success("还原完成");
      setStep(3);
    } catch (e) {
      message.error(`还原失败：${e instanceof Error ? e.message : "未知错误"}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Space direction="vertical" style={{ width: "100%" }} size="middle">
      <Card title="升级与规则包（离线迁移）">
        <Steps
          current={step}
          items={[{ title: "预检" }, { title: "打包" }, { title: "还原" }, { title: "完成" }]}
        />
        <Form form={preForm} layout="vertical" style={{ maxWidth: 560, marginTop: 24 }}>
          <Form.Item name="sourcePath" label="源路径（源节点上的应用目录）" rules={[{ required: true, message: "请输入源路径" }]}>
            <Input placeholder="如 /srv/app/myapp" />
          </Form.Item>
          <Form.Item name="targetPath" label="目标路径（目标节点上的还原目录）" rules={[{ required: true, message: "请输入目标路径" }]}>
            <Input placeholder="如 /srv/app/myapp" />
          </Form.Item>
          <Form.Item name="name" label="迁移包名" rules={[{ required: true, message: "请输入迁移包名" }]}>
            <Input placeholder="如 myapp-20261125" />
          </Form.Item>
          <Form.Item name="force" valuePropName="checked">
            <Checkbox>强制（忽略版本不一致）</Checkbox>
          </Form.Item>
        </Form>
        <Space style={{ marginBottom: 16 }}>
          <Button onClick={precheck} disabled={busy}>预检</Button>
          <Popconfirm title="确认打包源路径为迁移包？">
            <Button type="primary" onClick={pack} disabled={busy}>打包</Button>
          </Popconfirm>
        </Space>
        {precheckMsg && (
          <Alert type="success" message={`预检通过：${precheckMsg}`} style={{ marginBottom: 12 }} />
        )}
        {packedFile && (
          <Descriptions size="small" column={1} style={{ marginBottom: 12 }}>
            <Descriptions.Item label="迁移包">{packedFile}</Descriptions.Item>
          </Descriptions>
        )}

        {step >= 2 && (
          <Card type="inner" title="目标端还原" style={{ marginTop: 16 }}>
            <Space style={{ marginBottom: 12 }}>
              <Select
                style={{ minWidth: 220 }}
                value={targetNode || undefined}
                onChange={setTargetNode}
                options={nodes.map((n) => ({
                  value: n.nodeId,
                  label: `${n.hostname}${n.commandConnected ? "" : "（离线）"}`,
                }))}
                placeholder="选择目标节点"
              />
            </Space>
            <Form form={restoreForm} layout="vertical" style={{ maxWidth: 560 }}>
              <Form.Item name="targetPath" label="还原目标路径" rules={[{ required: true, message: "请输入还原目标路径" }]}>
                <Input placeholder="如 /srv/app/myapp" />
              </Form.Item>
              <Form.Item name="sourceVersion" label="源版本（可选，用于一致性校验）">
                <Input placeholder="留空跳过校验" />
              </Form.Item>
              <Form.Item name="force" valuePropName="checked">
                <Checkbox>强制还原（忽略版本不一致）</Checkbox>
              </Form.Item>
            </Form>
            <Popconfirm title="确认还原？目标路径同名文件将被覆盖（自动快照留档）。">
              <Button type="primary" danger onClick={restore} disabled={busy || !targetNode || !packedFile}>
                还原
              </Button>
            </Popconfirm>
            {restoreMsg && (
              <Typography.Paragraph type="secondary" style={{ marginTop: 12, fontSize: 12, whiteSpace: "pre-wrap" }}>
                {restoreMsg}
              </Typography.Paragraph>
            )}
          </Card>
        )}
      </Card>
    </Space>
  );
}
