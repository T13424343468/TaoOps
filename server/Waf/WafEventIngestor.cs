using System.IO;
using System.Text;
using TaoOps.Server.Services;

namespace TaoOps.Server.Waf;

/// <summary>
/// WAF 拦截日志回流：订阅节点日志流（复用 M2 LogStream 通道），按行解析
/// OpenResty WAF 事件（Lua 侧 TAOOPS_WAF JSON 行）落 WafEventStore。
/// 跨 chunk 半行有缓冲；畸形行跳过不崩。
/// </summary>
public sealed class WafEventIngestor
{
    /// <summary>Agent 侧 WAF 事件日志路径（生成器产物约定，OpenResty error_log 落此文件）。</summary>
    public const string DefaultWafLogPath = "/var/log/taoops-waf/events.log";

    private readonly NodeStreamHub _streams;
    private readonly WafEventStore _store;
    private readonly string _wafLogPath;

    public WafEventIngestor(NodeStreamHub streams, WafEventStore store, string? wafLogPath = null)
    {
        _streams = streams;
        _store = store;
        _wafLogPath = wafLogPath ?? Environment.GetEnvironmentVariable("TAOOPS_WAF_LOG_PATH") ?? DefaultWafLogPath;
    }

    /// <summary>开始回流某节点的 WAF 日志；返回订阅句柄，释放即停止。</summary>
    public async Task<IDisposable> StartAsync(string nodeId, CancellationToken ct)
    {
        var session = _streams.GetLog(nodeId)
            ?? throw new InvalidOperationException($"node {nodeId} log stream not attached");
        await session.Outbound.Writer.WriteAsync(new TaoOps.Server.Grpc.LogControl
        {
            Action = "subscribe",
            Path = _wafLogPath,
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

    /// <summary>
    /// 把一个 chunk 拆成完整行（含跨 chunk 半行缓冲），逐行解析为事件；
    /// 畸形行静默跳过。internal 便于单测直测解析逻辑。
    /// </summary>
    internal IEnumerable<WafEvent> ParseChunk(string text, StringBuilder tail, string nodeId)
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
            if (WafEventStore.TryParseEvent(line, nodeId) is { } ev)
                yield return ev;
        }
    }
}
