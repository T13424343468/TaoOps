using System.Text.Json;

namespace TaoOps.Server.Sec;

/// <summary>一条 SSH 防爆破锁定事件（由 Agent 回流的 TAOOPS_SEC 日志行解析而来）。</summary>
public sealed class SecEvent
{
    public string NodeId { get; set; } = "";
    /// <summary>事件类型，当前固定 ssh_lockout。</summary>
    public string Event { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Failures { get; set; }
    public long Ts { get; set; }
}

/// <summary>
/// SEC 事件账本：JSONL 追加落盘（dataDir/sec/events.jsonl）+ 内存查询，口径同 WafEventStore。
/// </summary>
public sealed class SecEventStore
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<SecEvent> _events = [];

    public SecEventStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "sec");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "events.jsonl");
        if (File.Exists(_filePath))
        {
            foreach (var line in File.ReadLines(_filePath))
            {
                try
                {
                    var ev = JsonSerializer.Deserialize<SecEvent>(line, JsonOpts);
                    if (ev is not null) _events.Add(ev);
                }
                catch (JsonException) { }
            }
        }
    }

    /// <summary>
    /// 解析一行 TAOOPS_SEC 日志为事件；畸形行返回 null（跳过不崩）。
    /// nodeId 由回流通道标注（日志行本身不含）。
    /// </summary>
    public static SecEvent? TryParseEvent(string line, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var payload = line;
        var marker = line.IndexOf("TAOOPS_SEC ", StringComparison.Ordinal);
        if (marker >= 0) payload = line[(marker + "TAOOPS_SEC ".Length)..].Trim();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string GetStr(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
            return new SecEvent
            {
                NodeId = nodeId,
                Event = GetStr("event"),
                Ip = GetStr("ip"),
                Failures = root.TryGetProperty("failures", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0,
                Ts = root.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number
                    ? ts.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>入库：落 JSONL 一行 + 内存索引。</summary>
    public void Append(SecEvent ev)
    {
        lock (_lock)
        {
            File.AppendAllText(_filePath, JsonSerializer.Serialize(ev, JsonOpts) + "\n");
            _events.Add(ev);
        }
    }

    /// <summary>查询：按节点与时间范围（毫秒）过滤，按时间倒序。</summary>
    public IReadOnlyList<SecEvent> Query(string? nodeId = null, long? fromMs = null, long? toMs = null)
    {
        lock (_lock)
        {
            return _events
                .Where(e => nodeId is null || e.NodeId == nodeId)
                .Where(e => fromMs is null || e.Ts >= fromMs)
                .Where(e => toMs is null || e.Ts <= toMs)
                .OrderByDescending(e => e.Ts)
                .ToList();
        }
    }
}
