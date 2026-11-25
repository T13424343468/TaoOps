using System.Text.Json;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A22 节点离线巡检（契约：告警体系闭环）：每 60s 检查全部节点命令通道
/// 状态，断连超过阈值（默认 300s，env TAOOPS_NODE_OFFLINE_THRESHOLD 可配）
/// 产生面板告警（接 FR-A19，级别 warn，边缘触发——恢复后复位才允许再次告
/// 警）；重新上线产生 info 级恢复记录（不推送，口径同 FR-A20 仅 critical 推送）。
/// </summary>
public sealed class NodeOfflineMonitor : BackgroundService
{
    /// <summary>巡检间隔。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>断连告警阈值（秒），env TAOOPS_NODE_OFFLINE_THRESHOLD 可配，默认 300。</summary>
    public static long OfflineThresholdSec =>
        long.TryParse(Environment.GetEnvironmentVariable("TAOOPS_NODE_OFFLINE_THRESHOLD"), out var v) && v > 0 ? v : 300;

    private readonly Func<IEnumerable<(string NodeId, bool Connected)>> _nodes;
    private readonly Alerts.AlertCenter _alerts;
    private readonly ILogger<NodeOfflineMonitor> _logger;
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _now;

    // Edge-trigger state per node: alerted until reconnect resets it.
    private readonly Dictionary<string, bool> _alerted = new(StringComparer.Ordinal);

    public NodeOfflineMonitor(Func<IEnumerable<(string NodeId, bool Connected)>> nodes,
        Alerts.AlertCenter alerts, ILogger<NodeOfflineMonitor> logger,
        TimeSpan? interval = null, Func<DateTimeOffset>? now = null)
    {
        _nodes = nodes;
        _alerts = alerts;
        _logger = logger;
        _interval = interval ?? Interval;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { PollOnce(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "node offline monitor poll failed"); }
        }
    }

    /// <summary>单轮巡检：检查各节点连接状态并按边缘触发口径告警与复位。测试直调。</summary>
    public void PollOnce()
    {
        var now = _now();
        var nowMs = now.ToUnixTimeMilliseconds();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (nodeId, connected) in _nodes())
        {
            seen.Add(nodeId);
            bool alerted = _alerted.TryGetValue(nodeId, out var a) && a;
            if (connected)
            {
                if (alerted)
                {
                    _alerted[nodeId] = false;
                    _alerts.Raise("node", Alerts.AlertLevels.Info, nodeId,
                        $"node reconnected (offline alert cleared at {now:O})", nowMs);
                }
            }
            else if (!alerted)
            {
                _alerted[nodeId] = true;
                _alerts.Raise("node", Alerts.AlertLevels.Warn, nodeId,
                    $"node command channel offline > {OfflineThresholdSec}s", nowMs);
            }
        }
        // Nodes removed from registry while alerted: clear edge state silently.
        foreach (var nodeId in _alerted.Keys.ToList())
        {
            if (!seen.Contains(nodeId)) _alerted.Remove(nodeId);
        }
    }
}
