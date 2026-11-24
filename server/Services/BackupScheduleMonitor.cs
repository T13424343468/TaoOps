using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A26 备份策略调度巡检：每 60s 对命令通道在线节点下发 backup.manage
/// op=policy_list，对到期的策略（interval: 到期间隔已过；daily: 今日未跑且
/// 已过计划时刻）下发 op=policy_run；执行失败产生 warn 告警（接 FR-A19，
/// 边缘触发——成功后复位）；节点离线时段不补跑。
/// </summary>
public sealed class BackupScheduleMonitor : BackgroundService
{
    /// <summary>巡检间隔（与 FR-A23 采样器同模式：60s）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly NodeCommandBridge _bridge;
    private readonly Func<IEnumerable<(string NodeId, bool Connected)>> _nodes;
    private readonly Alerts.AlertCenter _alerts;
    private readonly ILogger<BackupScheduleMonitor> _logger;
    private readonly TimeSpan _interval;
    private readonly Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>> _dispatch;
    private readonly Func<DateTimeOffset> _now;

    // Edge-trigger state per (node, policyId): alerted until a successful run resets it.
    private readonly Dictionary<string, bool> _alerted = new(StringComparer.Ordinal);

    public BackupScheduleMonitor(NodeCommandBridge bridge, Func<IEnumerable<(string NodeId, bool Connected)>> nodes,
        Alerts.AlertCenter alerts, ILogger<BackupScheduleMonitor> logger,
        TimeSpan? interval = null, Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>>? dispatch = null,
        Func<DateTimeOffset>? now = null)
    {
        _bridge = bridge;
        _nodes = nodes;
        _alerts = alerts;
        _logger = logger;
        _interval = interval ?? Interval;
        _dispatch = dispatch ?? ((nodeId, args, timeout) => _bridge.DispatchAsync(nodeId, "backup.manage", args, timeout, "system", "Admin", ""));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await PollOnce(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "backup schedule poll failed"); }
        }
    }

    /// <summary>单轮巡检：对在线节点列出策略，到期即触发执行并按边缘触发口径告警。测试直调。</summary>
    public async Task PollOnce(CancellationToken ct = default)
    {
        foreach (var (nodeId, connected) in _nodes())
        {
            if (!connected) continue;
            string output;
            int exit;
            try
            {
                var outcome = await _dispatch(nodeId, new[] { "op=policy_list" }, 30_000);
                output = outcome.Output;
                exit = outcome.ExitCode;
            }
            catch (BridgeException)
            {
                continue; // node unreachable this round; skip
            }
            if (exit != 0) continue;

            List<PolicyView>? policies;
            try { policies = JsonSerializer.Deserialize<List<PolicyView>>(output, JsonOpts); }
            catch (JsonException) { continue; }
            if (policies is null) continue;

            var now = _now();
            foreach (var p in policies)
            {
                if (string.IsNullOrEmpty(p.Id) || !p.Enabled || !p.Due(now)) continue;

                bool ok = false;
                string detail = "";
                try
                {
                    var run = await _dispatch(nodeId, new[] { "op=policy_run", $"id={p.Id}" }, 300_000);
                    ok = run.ExitCode == 0;
                    detail = run.Output;
                }
                catch (BridgeException ex)
                {
                    detail = ex.Message;
                }

                var key = $"{nodeId}\n{p.Id}";
                bool alerted = _alerted.TryGetValue(key, out var a) && a;
                if (ok)
                {
                    if (alerted) _alerted[key] = false; // recovered → reset edge state
                    continue;
                }
                if (!alerted)
                {
                    _alerted[key] = true;
                    _alerts.Raise("backup", Alerts.AlertLevels.Warn, nodeId,
                        $"backup policy {p.Name} failed: {detail}");
                }
            }
        }
    }

    private sealed class PolicyView
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("schedule")]
        public string Schedule { get; set; } = "";
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
        [JsonPropertyName("last_run_ms")]
        public long LastRunMs { get; set; }

        /// <summary>到期判定（与 Agent 侧 Policy.Due 同口径）。</summary>
        public bool Due(DateTimeOffset now)
        {
            if (!Enabled) return false;
            var last = DateTimeOffset.FromUnixTimeMilliseconds(LastRunMs);
            if (Schedule.StartsWith("interval:", StringComparison.Ordinal))
            {
                if (!int.TryParse(Schedule["interval:".Length..], out var sec) || sec < 60) return false;
                return LastRunMs == 0 || now - last >= TimeSpan.FromSeconds(sec);
            }
            if (Schedule.StartsWith("daily:", StringComparison.Ordinal))
            {
                var parts = Schedule["daily:".Length..].Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var hh) || !int.TryParse(parts[1], out var mm) ||
                    hh is < 0 or > 23 || mm is < 0 or > 59) return false;
                if (LastRunMs != 0 && last.Date == now.Date) return false;
                var todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, hh, mm, 0, now.Offset);
                return now >= todayAt;
            }
            return false;
        }
    }
}
