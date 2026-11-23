using System.Text.Json;

namespace TaoOps.Server.Alerts;

/// <summary>统一告警中心的一条告警（FR-A19）：落盘 dataDir/alerts/alerts.jsonl。</summary>
public sealed class Alert
{
    public string Id { get; set; } = "";
    /// <summary>来源：intrusion / disk / container / replication / cert / backup 等。</summary>
    public string Source { get; set; } = "";
    /// <summary>级别：info / warn / critical（映射表见 AlertLevels）。</summary>
    public string Level { get; set; } = "info";
    public string NodeId { get; set; } = "";
    /// <summary>人话描述，含关键数值。</summary>
    public string Detail { get; set; } = "";
    public long Ts { get; set; }
    public bool Read { get; set; }
}

/// <summary>
/// 告警级别与默认映射表（FR-A19 验收口径，PRD v1.2 §FR-A19）：
/// 磁盘水位 ≥90%=critical；入侵检测 high=critical / medium=warn / low=info；
/// 容器异常退出=warn；复制中断=warn；证书到期 ≤30 天=warn；备份失败=warn。
/// </summary>
public static class AlertLevels
{
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Critical = "critical";

    public static readonly IReadOnlyList<string> All = [Info, Warn, Critical];

    public static bool IsValid(string level) => All.Contains(level);

    /// <summary>入侵检测 severity → 统一告警级别。</summary>
    public static string FromIntrusionSeverity(string severity) => severity switch
    {
        "high" => Critical,
        "medium" => Warn,
        _ => Info,
    };
}

/// <summary>
/// 告警账本：JSONL 追加落盘（dataDir/alerts/alerts.jsonl）+ 内存查询，口径同 SecEventStore。
/// 保留策略默认 90 天（TAOOPS_ALERTS_RETENTION_DAYS 覆盖），启动与追加时清理过期条目。
/// </summary>
public sealed class AlertStore
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<Alert> _alerts = [];

    /// <summary>保留时长（毫秒），默认 90 天。</summary>
    public long RetentionMs { get; }

    public AlertStore(string dataDir)
    {
        var days = long.TryParse(Environment.GetEnvironmentVariable("TAOOPS_ALERTS_RETENTION_DAYS"), out var d) && d > 0
            ? d : 90;
        RetentionMs = days * 24 * 60 * 60 * 1000L;
        var dir = Path.Combine(dataDir, "alerts");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "alerts.jsonl");
        if (File.Exists(_filePath))
        {
            foreach (var line in File.ReadLines(_filePath))
            {
                try
                {
                    var alert = JsonSerializer.Deserialize<Alert>(line, JsonOpts);
                    if (alert is not null) _alerts.Add(alert);
                }
                catch (JsonException) { }
            }
        }
        Prune(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>入库：落 JSONL 一行 + 内存索引；Id 由中控生成；追加前清理过期条目（保留策略）。</summary>
    public Alert Append(string source, string level, string nodeId, string detail, long? tsMs = null)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid().ToString("N"),
            Source = source,
            Level = level,
            NodeId = nodeId,
            Detail = detail,
            Ts = tsMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        lock (_lock)
        {
            // 保留策略按当前时钟清理（显式传入的历史时间戳同样受保留约束）。
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var expired = _alerts.RemoveAll(a => a.Ts < nowMs - RetentionMs);
            if (expired > 0) RewriteFile();
            File.AppendAllText(_filePath, JsonSerializer.Serialize(alert, JsonOpts) + "\n");
            _alerts.Add(alert);
        }
        return alert;
    }

    /// <summary>查询：按级别/来源/节点/时间范围/未读过滤，时间倒序。</summary>
    public IReadOnlyList<Alert> Query(string? level = null, string? source = null, string? nodeId = null,
        long? fromMs = null, long? toMs = null, bool? unreadOnly = null)
    {
        lock (_lock)
        {
            return _alerts
                .Where(a => level is null || a.Level == level)
                .Where(a => source is null || a.Source == source)
                .Where(a => nodeId is null || a.NodeId == nodeId)
                .Where(a => fromMs is null || a.Ts >= fromMs)
                .Where(a => toMs is null || a.Ts <= toMs)
                .Where(a => unreadOnly is null || !unreadOnly.Value || !a.Read)
                .OrderByDescending(a => a.Ts)
                .ToList();
        }
    }

    /// <summary>未读数（面板角标）。</summary>
    public int UnreadCount()
    {
        lock (_lock) return _alerts.Count(a => !a.Read);
    }

    /// <summary>标记单条已读；未知 Id 返回 false。</summary>
    public bool MarkRead(string id)
    {
        lock (_lock)
        {
            var alert = _alerts.FirstOrDefault(a => a.Id == id);
            if (alert is null) return false;
            alert.Read = true;
            RewriteFile();
            return true;
        }
    }

    /// <summary>全部标记已读，返回标记条数。</summary>
    public int MarkAllRead()
    {
        lock (_lock)
        {
            var marked = 0;
            foreach (var a in _alerts)
            {
                if (!a.Read)
                {
                    a.Read = true;
                    marked++;
                }
            }
            if (marked > 0) RewriteFile();
            return marked;
        }
    }

    private void Prune(long nowMs)
    {
        lock (_lock)
        {
            var before = _alerts.Count;
            _alerts.RemoveAll(a => a.Ts < nowMs - RetentionMs);
            if (_alerts.Count != before) RewriteFile();
        }
    }

    /// <summary>全量重写（90 天告警量级 MB 内，口径同 WafEventStore 导出）。</summary>
    private void RewriteFile()
    {
        using var writer = new StreamWriter(_filePath, append: false);
        foreach (var a in _alerts)
            writer.WriteLine(JsonSerializer.Serialize(a, JsonOpts));
    }
}
