import { useEffect, useRef, useState } from "react";
import { Button, Input, Select, Space, Typography } from "antd";
import { wsUrl } from "../api/client";
import { mockNodes } from "../api/mock";

// Real WebSocket terminal: /ws/agent/{nodeId}/terminal (protocol contract §1).
export default function TermPage() {
  const [nodeId, setNodeId] = useState(mockNodes()[0]?.nodeId ?? "node-01");
  const [lines, setLines] = useState<string[]>([]);
  const [input, setInput] = useState("");
  const [connected, setConnected] = useState(false);
  const wsRef = useRef<WebSocket | null>(null);
  const boxRef = useRef<HTMLDivElement>(null);

  useEffect(() => () => wsRef.current?.close(), []);

  const connect = () => {
    wsRef.current?.close();
    setLines([]);
    const ws = new WebSocket(wsUrl(nodeId, "terminal"));
    wsRef.current = ws;
    ws.onopen = () => { setConnected(true); setLines((l) => [...l, `-- connected ${nodeId} --`]); };
    ws.onclose = () => setConnected(false);
    ws.onerror = () => setLines((l) => [...l, "-- websocket error（节点离线或通道不可用）--"]);
    ws.onmessage = (ev) => setLines((l) => [...l.slice(-500), ev.data]);
  };

  const send = () => {
    if (!input.trim()) return;
    wsRef.current?.send(input + "\n");
    setInput("");
  };

  useEffect(() => {
    const box = boxRef.current;
    if (box) box.scrollTop = box.scrollHeight; // jsdom lacks scrollTo
  }, [lines]);

  return (
    <div>
      <Typography.Title level={4}>终端</Typography.Title>
      <Space style={{ marginBottom: 8 }}>
        <Select value={nodeId} onChange={setNodeId} style={{ width: 200 }}
          options={mockNodes().map((n) => ({ value: n.nodeId, label: `${n.hostname} (${n.nodeId})` }))} />
        <Button type="primary" onClick={connect}>{connected ? "重连" : "连接节点"}</Button>
      </Space>
      <div ref={boxRef} style={{ background: "#000", color: "#0f0", fontFamily: "monospace", fontSize: 13, height: 420, overflow: "auto", padding: 8, borderRadius: 4, whiteSpace: "pre-wrap" }}>
        {lines.join("\n")}
      </div>
      <Space.Compact style={{ width: "100%", marginTop: 8 }}>
        <Input
          value={input}
          onChange={(e: React.ChangeEvent<HTMLInputElement>) => setInput(e.target.value)}
          onPressEnter={send}
          placeholder={connected ? "输入命令回车发送" : "先连接节点"}
          disabled={!connected}
        />
        <Button onClick={send} disabled={!connected}>发送</Button>
      </Space.Compact>
    </div>
  );
}
