using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaoOps.Server.Waf;

/// <summary>一次 WAF 拦截/记录事件（由 Agent 回流的 OpenResty 日志行解析而来）。</summary>
public sealed class WafEvent
{
    public string NodeId { get; set; } = "";
    /// <summary>deny=拦截 / log=仅记录，对应 Lua 侧 event 字段。</summary>
    public string Event { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Uri { get; set; } = "";
    /// <summary>归一化后的 URI（Lua 侧匹配副本）：与原始 Uri 双值对账，便于回溯误报。</summary>
    public string NormUri { get; set; } = "";
    /// <summary>事件时间（Unix 毫秒）：优先取日志行自带 ts，缺省取入库时刻。</summary>
    public long Ts { get; set; }
}

/// <summary>
/// WAF 事件账本：JSONL 追加落盘（dataDir/waf/events.jsonl）+ 内存查询。
/// 只追加不重写，事件流水天然原子。
/// </summary>
public sealed class WafEventStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<WafEvent> _events = [];

    public WafEventStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "waf");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "events.jsonl");
        if (File.Exists(_filePath))
        {
            // 重载解析与落盘对称（Append 写 WafEvent JSON），解析失败的行跳过不崩。
            foreach (var line in File.ReadLines(_filePath))
            {
                try
                {
                    var ev = JsonSerializer.Deserialize<WafEvent>(line, JsonOpts);
                    if (ev is not null) _events.Add(ev);
                }
                catch (JsonException) { }
            }
        }
    }

    /// <summary>
    /// 解析一行 OpenResty WAF 日志（Lua 侧 TAOOPS_WAF 前缀 JSON 或纯 JSON 行）为事件；
    /// 畸形行返回 null（跳过不崩）。nodeId 由回流通道标注（日志行本身不含）。
    /// </summary>
    public static WafEvent? TryParseEvent(string line, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var payload = line;
        var marker = line.IndexOf("TAOOPS_WAF ", StringComparison.Ordinal);
        if (marker >= 0) payload = line[(marker + "TAOOPS_WAF ".Length)..].Trim();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string GetStr(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
            return new WafEvent
            {
                NodeId = nodeId,
                Event = GetStr("event"),
                RuleId = GetStr("rule_id"),
                RuleName = GetStr("rule_name"),
                Ip = GetStr("ip"),
                Uri = GetStr("uri"),
                NormUri = GetStr("norm_uri"),
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
    public void Append(WafEvent ev)
    {
        lock (_lock)
        {
            File.AppendAllText(_filePath, JsonSerializer.Serialize(ev, JsonOpts) + "\n");
            _events.Add(ev);
        }
    }

    /// <summary>查询：按节点与时间范围（毫秒）过滤，按时间倒序。</summary>
    public IReadOnlyList<WafEvent> Query(string? nodeId = null, long? fromMs = null, long? toMs = null)
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
