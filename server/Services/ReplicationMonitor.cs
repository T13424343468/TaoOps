using System.Text.Json;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A15 复制中断巡检（契约 §13）：每 60s 对全部命令通道在线节点下发
/// db.manage op=replica_status，IO/SQL 线程非 Yes 或延迟超阈值产生面板
/// 告警（接 FR-A19，级别 warn，边缘触发——恢复后复位才允许再次告警）。
/// </summary>
public sealed class ReplicationMonitor : BackgroundService
{
    /// <summary>巡检间隔（PRD：60s）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>延迟告警阈值（秒），env TAOOPS_REPLICA_LAG_THRESHOLD 可配，默认 300。</summary>
    public static long LagThresholdSec =>
        long.TryParse(Environment.GetEnvironmentVariable("TAOOPS_REPLICA_LAG_THRESHOLD"), out var v) && v > 0 ? v : 300;

    private readonly NodeCommandBridge _bridge;
    private readonly Func<IEnumerable<(string NodeId, bool Connected)>> _nodes;
    private readonly Alerts.AlertCenter _alerts;
    private readonly ILogger<ReplicationMonitor> _logger;
    private readonly TimeSpan _interval;
    private readonly Func<string, string, int, Task<NodeCommandBridge.CommandOutcome>> _dispatch;

    // Edge-trigger state per node: alerted until a healthy poll resets it.
    private readonly Dictionary<string, bool> _threadAlerted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _lagAlerted = new(StringComparer.Ordinal);

    public ReplicationMonitor(NodeCommandBridge bridge, Func<IEnumerable<(string NodeId, bool Connected)>> nodes,
        Alerts.AlertCenter alerts, ILogger<ReplicationMonitor> logger,
        TimeSpan? interval = null, Func<string, string, int, Task<NodeCommandBridge.CommandOutcome>>? dispatch = null)
    {
        _bridge = bridge;
        _nodes = nodes;
        _alerts = alerts;
        _logger = logger;
        _interval = interval ?? Interval;
        _dispatch = dispatch ?? ((nodeId, cmd, timeout) => _bridge.DispatchAsync(nodeId, cmd, new[] { "op=replica_status" }, timeout, "system", "Admin", ""));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await PollOnce(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "replication monitor poll failed"); }
        }
    }

    /// <summary>单轮巡检：查询各节点复制状态并按边缘触发口径告警。测试直调。</summary>
    public async Task PollOnce(CancellationToken ct = default)
    {
        foreach (var (nodeId, connected) in _nodes())
        {
            if (!connected) continue;
            string output;
            int exit;
            try
            {
                var outcome = await _dispatch(nodeId, "db.manage", 30_000);
                output = outcome.Output;
                exit = outcome.ExitCode;
            }
            catch (BridgeException)
            {
                continue; // node unreachable this round; skip
            }
            if (exit != 0) continue;

            SlaveView? st;
            try { st = JsonSerializer.Deserialize<SlaveView>(output); }
            catch (JsonException) { continue; }
            if (st is null) continue;

            bool threadsOk = !st.Configured || (st.IoRunning == "Yes" && st.SqlRunning == "Yes");
            bool lagOk = st.SecondsBehindMaster <= LagThresholdSec;

            bool threadAlerted = _threadAlerted.TryGetValue(nodeId, out var ta) && ta;
            if (!threadsOk && !threadAlerted)
            {
                _threadAlerted[nodeId] = true;
                _alerts.Raise("replication", Alerts.AlertLevels.Warn, nodeId,
                    $"replication thread down (io={st.IoRunning}, sql={st.SqlRunning}){LastError(st)}");
            }
            else if (threadsOk && threadAlerted)
            {
                _threadAlerted[nodeId] = false;
            }

            bool lagAlerted = _lagAlerted.TryGetValue(nodeId, out var la) && la;
            if (!lagOk && !lagAlerted)
            {
                _lagAlerted[nodeId] = true;
                _alerts.Raise("replication", Alerts.AlertLevels.Warn, nodeId,
                    $"replica lag {st.SecondsBehindMaster}s > {LagThresholdSec}s");
            }
            else if (lagOk && lagAlerted)
            {
                _lagAlerted[nodeId] = false;
            }
        }
    }

    private static string LastError(SlaveView st) =>
        string.IsNullOrEmpty(st.LastError) ? "" : $" last_error={st.LastError}";

    private sealed class SlaveView
    {
        [System.Text.Json.Serialization.JsonPropertyName("configured")]
        public bool Configured { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("io_running")]
        public string IoRunning { get; set; } = "No";
        [System.Text.Json.Serialization.JsonPropertyName("sql_running")]
        public string SqlRunning { get; set; } = "No";
        [System.Text.Json.Serialization.JsonPropertyName("seconds_behind_master")]
        public long SecondsBehindMaster { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("last_error")]
        public string LastError { get; set; } = "";
    }
}
