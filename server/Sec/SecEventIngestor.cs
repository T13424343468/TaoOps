using System.Text;
using TaoOps.Server.Services;

namespace TaoOps.Server.Sec;

/// <summary>
/// SEC 事件回流：订阅节点日志流（复用 M2 LogStream 通道），按行解析
/// Agent secguard 的 TAOOPS_SEC JSONL 锁定事件落 SecEventStore。
/// 口径同 WafEventIngestor：跨 chunk 半行缓冲，畸形行跳过。
/// </summary>
public sealed class SecEventIngestor
{
    /// <summary>Agent 侧 SEC 事件日志路径（secguard 默认产物）。</summary>
    public const string DefaultSecLogPath = "/var/log/taoops-sec/events.log";

    private readonly NodeStreamHub _streams;
    private readonly SecEventStore _store;
    private readonly string _secLogPath;

    public SecEventIngestor(NodeStreamHub streams, SecEventStore store, string? secLogPath = null)
    {
        _streams = streams;
        _store = store;
        _secLogPath = secLogPath ?? Environment.GetEnvironmentVariable("TAOOPS_SEC_LOG_PATH") ?? DefaultSecLogPath;
    }

    /// <summary>开始回流某节点的 SEC 日志；返回订阅句柄，释放即停止。</summary>
    public async Task<IDisposable> StartAsync(string nodeId, CancellationToken ct)
    {
        var session = _streams.GetLog(nodeId)
            ?? throw new InvalidOperationException($"node {nodeId} log stream not attached");
        await session.Outbound.Writer.WriteAsync(new TaoOps.Server.Grpc.LogControl
        {
            Action = "subscribe",
            Path = _secLogPath,
            TailLines = 0,
        }, ct);

        using var sub = session.Subscribe(out var reader);
        var tail = new StringBuilder();
        await foreach (var chunk in reader.ReadAllAsync(ct))
        {
            var text = Encoding.UTF8.GetString(chunk.Data.ToArray());
            foreach (var ev in ParseChunk(text, tail, nodeId))
                _store.Append(ev);
        }
        return sub;
    }

    /// <summary>把一个 chunk 拆成完整行（含跨 chunk 半行缓冲），逐行解析；畸形行静默跳过。</summary>
    internal IEnumerable<SecEvent> ParseChunk(string text, StringBuilder tail, string nodeId)
    {
        tail.Append(text);
        while (true)
        {
            var buf = tail.ToString();
            var nl = buf.IndexOf('\n');
            if (nl < 0) yield break;
            var line = buf[..nl].TrimEnd('\r');
            tail.Clear();
            tail.Append(buf[(nl + 1)..]);
            if (SecEventStore.TryParseEvent(line, nodeId) is { } ev)
                yield return ev;
        }
    }
}
