using System.Text.Json;
using TaoOps.Server.Audit;

namespace TaoOps.Server.Sec;

/// <summary>防入侵告警类型（A8-A10）。</summary>
public static class SecAlertTypes
{
    public const string LoginHour = "login_hour";
    public const string UnusualAction = "unusual_action";
    public const string BruteforceAgg = "bruteforce_agg";
}

/// <summary>一条防入侵告警（A11：可查可溯，全部进审计哈希链）。</summary>
public sealed class SecAlert
{
    public string Type { get; set; } = "";
    /// <summary>严重级：low / medium / high。</summary>
    public string Severity { get; set; } = "low";
    public string User { get; set; } = "";
    public string Ip { get; set; } = "";
    public string NodeId { get; set; } = "";
    /// <summary>触发依据（人话描述，含关键数值）。</summary>
    public string Detail { get; set; } = "";
    public long Ts { get; set; }
    /// <summary>审计链中对应条目的 Seq（溯源入口）。</summary>
    public long AuditSeq { get; set; }
}

/// <summary>
/// 防入侵检测器（A8-A11）：异常登录时段、非常规命令、暴力尝试聚合。
/// 每条告警经 AuditStore 落 FR-D4 审计哈希链（Action=sec.alert），
/// 篡改可由 VerifyChain 检出。旋钮集中（编码规范一.5）：环境变量覆盖，默认从严。
/// </summary>
public sealed class IntrusionDetector
{
    private readonly AuditStore _audit;
    private readonly object _lock = new();

    // A9：用户 → 已见过的 action 集合（首次执行新 action 才告警）。
    private readonly Dictionary<string, HashSet<string>> _seenActions = new(StringComparer.Ordinal);
    // A10：源 IP → 窗口内失败/锁定事件时间戳（达阈值后清零重计，窗口内不重复）。
    private readonly Dictionary<string, List<long>> _ipFailures = new(StringComparer.Ordinal);
    // A11：告警内存索引（审计链为真源，此处为查询副本）。
    private readonly List<SecAlert> _alerts = [];

    public IntrusionDetector(AuditStore audit) => _audit = audit;

    // ---- 旋钮（环境变量覆盖，默认从严）----

    /// <summary>静默时段（本地时区小时）：默认 23-06，支持跨零点窗口。</summary>
    public (int Start, int End) QuietHours { get; } = ParseQuietHours(
        Environment.GetEnvironmentVariable("TAOOPS_SEC_QUIET_HOURS") ?? "23-06");

    /// <summary>暴力尝试聚合窗口（毫秒）：默认 15 分钟。</summary>
    public long AggWindowMs { get; } = long.TryParse(
        Environment.GetEnvironmentVariable("TAOOPS_SEC_AGG_WINDOW_MS"), out var w) && w > 0
        ? w : 15 * 60 * 1000;

    /// <summary>暴力尝试聚合阈值：默认 3。</summary>
    public int AggThreshold { get; } = int.TryParse(
        Environment.GetEnvironmentVariable("TAOOPS_SEC_AGG_THRESHOLD"), out var t) && t > 0
        ? t : 3;

    internal static (int Start, int End) ParseQuietHours(string raw)
    {
        var parts = raw.Split('-');
        if (parts.Length == 2 && int.TryParse(parts[0], out var s) && int.TryParse(parts[1], out var e)
            && s is >= 0 and <= 23 && e is >= 0 and <= 23)
            return (s, e);
        return (23, 6);
    }

    // ---- A8 异常登录时段 ----

    /// <summary>
    /// 成功登录落账：发生在静默时段之外 → sec.alert(type=login_hour, severity=medium)。
    /// hour 落在 [start,end) 窗口内（支持跨零点）视为静默正常；窗口外即异常。
    /// </summary>
    public SecAlert OnLoginSuccess(string user, string ip, long tsMs, int? hourOverride = null)
    {
        var hour = hourOverride ?? DateTimeOffset.FromUnixTimeMilliseconds(tsMs).LocalDateTime.Hour;
        var (start, end) = QuietHours;
        bool inQuiet = start == end
            ? true
            : start < end
                ? hour >= start && hour < end
                : hour >= start || hour < end;
        if (inQuiet)
            return NoAlert(SecAlertTypes.LoginHour, tsMs);
        return Emit(SecAlertTypes.LoginHour, "medium", user, ip, nodeId: "",
            detail: $"login outside quiet hours {start:D2}-{end:D2} (hour={hour})", tsMs);
    }

    // ---- A9 非常规命令 ----

    /// <summary>
    /// 用户执行动作落账：该用户历史未见过的 action → sec.alert(type=unusual_action, severity=low)；
    /// 同用户重复执行同 action 不重复告警。
    /// </summary>
    public SecAlert OnUserAction(string user, string action, string ip, long tsMs)
    {
        bool first;
        lock (_lock)
        {
            if (!_seenActions.TryGetValue(user, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _seenActions[user] = set;
            }
            first = set.Add(action);
        }
        if (!first)
            return NoAlert(SecAlertTypes.UnusualAction, tsMs);
        return Emit(SecAlertTypes.UnusualAction, "low", user, ip, nodeId: "",
            detail: $"first-time action {action}", tsMs);
    }

    // ---- A10 暴力尝试聚合 ----

    /// <summary>
    /// SEC 失败/锁定事件（Agent 回流 ssh_lockout 与面板侧登录锁定同口径计入）：
    /// 同源 IP 窗口内达阈值 → 聚合为一条 sec.alert(type=bruteforce_agg, severity=high)，
    /// 告警后窗口清零重计，窗口内不重复。
    /// </summary>
    public SecAlert OnSecFailure(string ip, long tsMs, string source = "agent")
    {
        bool threshold;
        lock (_lock)
        {
            if (!_ipFailures.TryGetValue(ip, out var list))
            {
                list = [];
                _ipFailures[ip] = list;
            }
            list.Add(tsMs);
            list.RemoveAll(t => t < tsMs - AggWindowMs);
            threshold = list.Count >= AggThreshold;
            if (threshold) list.Clear();
        }
        if (!threshold)
            return NoAlert(SecAlertTypes.BruteforceAgg, tsMs);
        return Emit(SecAlertTypes.BruteforceAgg, "high", user: "", ip, nodeId: "",
            detail: $"{AggThreshold}+ failures from {ip} within {AggWindowMs}ms (source={source})", tsMs);
    }

    // ---- A11 查询 ----

    /// <summary>按 type/severity/时间范围（毫秒）过滤查询告警，时间倒序。</summary>
    public IReadOnlyList<SecAlert> Query(string? type = null, string? severity = null,
        long? fromMs = null, long? toMs = null)
    {
        lock (_lock)
        {
            return _alerts
                .Where(a => type is null || a.Type == type)
                .Where(a => severity is null || a.Severity == severity)
                .Where(a => fromMs is null || a.Ts >= fromMs)
                .Where(a => toMs is null || a.Ts <= toMs)
                .OrderByDescending(a => a.Ts)
                .ToList();
        }
    }

    // ---- 内部 ----

    private static SecAlert NoAlert(string type, long tsMs) => new() { Type = type, Ts = tsMs, Detail = "(no alert)" };

    private SecAlert Emit(string type, string severity, string user, string ip, string nodeId, string detail, long tsMs)
    {
        var audit = _audit.Append(new AuditEvent
        {
            Ts = tsMs,
            User = string.IsNullOrEmpty(user) ? "(system)" : user,
            Role = "sec",
            Action = "sec.alert",
            Target = $"{type}:{detail}",
            Ip = ip,
            Result = "alerted",
        });
        var alert = new SecAlert
        {
            Type = type,
            Severity = severity,
            User = user,
            Ip = ip,
            NodeId = nodeId,
            Detail = detail,
            Ts = tsMs,
            AuditSeq = audit.Seq,
        };
        lock (_lock)
        {
            _alerts.Add(alert);
        }
        return alert;
    }
}
