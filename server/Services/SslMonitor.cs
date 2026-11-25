using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A21 证书到期巡检（契约 §14）：每 24h 对全部命令通道在线节点下发
/// ssl.manage op=list；证书 not_after ≤30 天 → 触发 op=renew，续期失败或
/// 仍到期 → 告警 ssl 级别 warn（接 FR-A19，边缘触发——续期成功或证书更新
/// 后复位）；&gt;30 天不动作。
/// </summary>
public sealed class SslMonitor : BackgroundService
{
    /// <summary>巡检间隔（契约：每 24h）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>续期窗口（契约：到期前 30 天）。</summary>
    public static readonly TimeSpan RenewWindow = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly NodeCommandBridge _bridge;
    private readonly Func<IEnumerable<(string NodeId, bool Connected)>> _nodes;
    private readonly Alerts.AlertCenter _alerts;
    private readonly ILogger<SslMonitor> _logger;
    private readonly TimeSpan _interval;
    private readonly Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>> _dispatch;
    private readonly Func<DateTimeOffset> _now;

    // Edge-trigger state per (node, domain): alerted until a healthy check resets it.
    private readonly Dictionary<string, bool> _alerted = new(StringComparer.Ordinal);

    public SslMonitor(NodeCommandBridge bridge, Func<IEnumerable<(string NodeId, bool Connected)>> nodes,
        Alerts.AlertCenter alerts, ILogger<SslMonitor> logger,
        TimeSpan? interval = null, Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>>? dispatch = null,
        Func<DateTimeOffset>? now = null)
    {
        _bridge = bridge;
        _nodes = nodes;
        _alerts = alerts;
        _logger = logger;
        _interval = interval ?? Interval;
        _dispatch = dispatch ?? ((nodeId, args, timeout) => _bridge.DispatchAsync(nodeId, "ssl.manage", args, timeout, "system", "Admin", ""));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await PollOnce(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "ssl monitor poll failed"); }
        }
    }

    /// <summary>单轮巡检：检查各节点证书到期状态并按边缘触发口径续期与告警。测试直调。</summary>
    public async Task PollOnce(CancellationToken ct = default)
    {
        foreach (var (nodeId, connected) in _nodes())
        {
            if (!connected) continue;
            string output;
            int exit;
            try
            {
                var outcome = await _dispatch(nodeId, new[] { "op=list" }, 30_000);
                output = outcome.Output;
                exit = outcome.ExitCode;
            }
            catch (BridgeException)
            {
                continue; // node unreachable this round; skip
            }
            if (exit != 0) continue;

            List<CertView>? certs;
            try { certs = JsonSerializer.Deserialize<List<CertView>>(output, JsonOpts); }
            catch (JsonException) { continue; }
            if (certs is null) continue;

            var now = _now();
            foreach (var cert in certs)
            {
                if (string.IsNullOrEmpty(cert.Domain) || !DateTimeOffset.TryParse(cert.NotAfter, out var notAfter))
                    continue;
                var key = $"{nodeId}\n{cert.Domain}";
                bool expiring = notAfter - now <= RenewWindow;
                bool alerted = _alerted.TryGetValue(key, out var a) && a;

                if (!expiring)
                {
                    if (alerted) _alerted[key] = false; // renewed or cert updated → reset
                    continue;
                }

                // Within the renewal window: attempt renew first.
                bool renewed = false;
                try
                {
                    var renewOutcome = await _dispatch(nodeId, new[] { "op=renew", $"domain={cert.Domain}" }, 120_000);
                    renewed = renewOutcome.ExitCode == 0;
                }
                catch (BridgeException)
                {
                    renewed = false;
                }

                if (renewed && !alerted)
                {
                    // Renewal succeeded before any alert was needed.
                    continue;
                }
                if (!renewed && !alerted)
                {
                    _alerted[key] = true;
                    _alerts.Raise("ssl", Alerts.AlertLevels.Warn, nodeId,
                        $"certificate {cert.Domain} expires {cert.NotAfter} and renewal failed");
                }
                // renewed && alerted: renewal succeeded → reset edge state.
                if (renewed && alerted) _alerted[key] = false;
            }
        }
    }

    private sealed class CertView
    {
        [JsonPropertyName("domain")]
        public string Domain { get; set; } = "";
        [JsonPropertyName("cert_path")]
        public string CertPath { get; set; } = "";
        [JsonPropertyName("issuer")]
        public string Issuer { get; set; } = "";
        [JsonPropertyName("not_after")]
        public string NotAfter { get; set; } = "";
    }
}
