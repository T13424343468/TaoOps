using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A23 资源实时监控采样器：每 60s 对命令通道在线节点下发 sysinfo.overview
/// op=metrics_sample，解析快照写入 MetricsStore。采样失败静默（PRD 口径）——
/// 留空档不存垃圾，也不产生告警。
/// </summary>
public sealed class MetricsSampler : BackgroundService
{
    /// <summary>采样间隔（60s，与备份调度巡检同模式）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly NodeCommandBridge _bridge;
    private readonly Func<IEnumerable<(string NodeId, bool Connected)>> _nodes;
    private readonly MetricsStore _store;
    private readonly ILogger<MetricsSampler> _logger;
    private readonly TimeSpan _interval;
    private readonly Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>> _dispatch;
    private readonly Func<DateTimeOffset> _now;

    public MetricsSampler(NodeCommandBridge bridge, Func<IEnumerable<(string NodeId, bool Connected)>> nodes,
        MetricsStore store, ILogger<MetricsSampler> logger, TimeSpan? interval = null,
        Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>>? dispatch = null,
        Func<DateTimeOffset>? now = null)
    {
        _bridge = bridge;
        _nodes = nodes;
        _store = store;
        _logger = logger;
        _interval = interval ?? Interval;
        _dispatch = dispatch ?? ((nodeId, args, timeout) => _bridge.DispatchAsync(nodeId, "sysinfo.overview", args, timeout, "system", "Admin", ""));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await PollOnce(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "metrics sample poll failed"); }
        }
    }

    /// <summary>单轮采样：对在线节点取快照入库，失败静默跳过。测试直调。</summary>
    public async Task PollOnce(CancellationToken ct = default)
    {
        foreach (var (nodeId, connected) in _nodes())
        {
            if (!connected) continue;
            string output;
            int exit;
            try
            {
                var outcome = await _dispatch(nodeId, new[] { "op=metrics_sample" }, 30_000);
                output = outcome.Output;
                exit = outcome.ExitCode;
            }
            catch (BridgeException)
            {
                continue; // node unreachable this round; silent gap
            }
            if (exit != 0) continue;

            SampleView? s;
            try { s = JsonSerializer.Deserialize<SampleView>(output, JsonOpts); }
            catch (JsonException) { continue; }
            if (s is null) continue;

            _store.Add(nodeId, new MetricsStore.MetricPoint(
                _now().ToUnixTimeMilliseconds(),
                s.CpuUsedPct, s.MemTotalKB, s.MemUsedKB,
                s.DiskReadBps, s.DiskWriteBps, s.NetRxTotal, s.NetTxTotal,
                s.DiskUsedKB, s.DiskTotalKB));
        }
    }

    private sealed class SampleView
    {
        [JsonPropertyName("cpu_used_pct")] public double CpuUsedPct { get; set; }
        [JsonPropertyName("mem_total_kb")] public ulong MemTotalKB { get; set; }
        [JsonPropertyName("mem_used_kb")] public ulong MemUsedKB { get; set; }
        [JsonPropertyName("disk_read_bps")] public ulong DiskReadBps { get; set; }
        [JsonPropertyName("disk_write_bps")] public ulong DiskWriteBps { get; set; }
        [JsonPropertyName("net_rx_total")] public ulong NetRxTotal { get; set; }
        [JsonPropertyName("net_tx_total")] public ulong NetTxTotal { get; set; }
        [JsonPropertyName("disk_used_kb")] public ulong DiskUsedKB { get; set; }
        [JsonPropertyName("disk_total_kb")] public ulong DiskTotalKB { get; set; }
    }
}
