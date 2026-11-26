using System.Text.Json;
using System.Text.Json.Serialization;
using TaoOps.Server.Alerts;

namespace TaoOps.Server.Services;

/// <summary>
/// IAdviceDataSource 默认实现：从既有 Store 与下发桥取数（PRD v1.4 §一）。
/// 容器重启计数走 docker.manage op=inspect（dockerd 原生 RestartCount，架构位评审口径），
/// 证书清单走 ssl.manage op=list（FR-A21 通道），备份策略走 backup.manage op=policy_list（FR-A26 通道）。
/// 任一数据源失败按「数据不足」处理，不阻塞其余规则。
/// </summary>
public class DefaultAdviceDataSource(
    MetricsStore metrics,
    AlertStore alerts,
    NodeCommandBridge bridge,
    Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>>? dispatch = null) : IAdviceDataSource
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly Func<string, string[], int, Task<NodeCommandBridge.CommandOutcome>> _dispatch =
        dispatch ?? ((nodeId, args, timeout) => bridge.DispatchAsync(nodeId, "docker.manage", args, timeout, "system", "Admin", ""));

    public IReadOnlyList<MetricsStore.MetricPoint> GetMetrics(string nodeId, long sinceMs) =>
        metrics.Query(nodeId, sinceMs);

    public IReadOnlyList<Alert> GetAlertHistory(string nodeId, long sinceMs) =>
        alerts.Query(nodeId: nodeId).Where(a => a.Ts >= sinceMs).ToList();

    public IReadOnlyList<CertStatusInfo> GetCertStatus(string nodeId)
    {
        try
        {
            var outcome = DispatchSync(nodeId, "ssl.manage", "op=list");
            if (outcome?.ExitCode != 0) return [];
            var certs = JsonSerializer.Deserialize<List<SslCertView>>(outcome!.Output, JsonOpts) ?? [];
            return certs.Select(c => new CertStatusInfo(
                c.Domain,
                DateTimeOffset.TryParse(c.NotAfter, out var t) ? t : DateTimeOffset.MinValue,
                AutoRenewConfigured: false)).ToList();
        }
        catch (Exception)
        {
            return []; // 数据不足
        }
    }

    public int GetBackupCoverage(string nodeId)
    {
        try
        {
            var outcome = DispatchSync(nodeId, "backup.manage", "op=policy_list");
            if (outcome?.ExitCode != 0) return -1;
            var policies = JsonSerializer.Deserialize<List<BackupPolicyView>>(outcome!.Output, JsonOpts) ?? [];
            return policies.Count(p => p.Enabled);
        }
        catch (Exception)
        {
            return -1; // 数据不足
        }
    }

    public async Task<int> GetContainerRestarts(string nodeId, CancellationToken ct = default)
    {
        try
        {
            var outcome = await _dispatch(nodeId, new[] { "op=inspect" }, 30_000);
            if (outcome.ExitCode != 0) return -1;
            var view = JsonSerializer.Deserialize<RestartView>(outcome.Output, JsonOpts);
            return view?.TotalRestarts ?? -1;
        }
        catch (Exception)
        {
            return -1; // 数据不足（节点离线/无容器/超时）
        }
    }

    private NodeCommandBridge.CommandOutcome? DispatchSync(string nodeId, string module, string arg)
    {
        return _dispatch(nodeId, new[] { arg }, 30_000).GetAwaiter().GetResult();
    }

    private sealed class SslCertView
    {
        [JsonPropertyName("domain")] public string Domain { get; set; } = "";
        [JsonPropertyName("not_after")] public string NotAfter { get; set; } = "";
    }

    private sealed class BackupPolicyView
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    }

    private sealed class RestartView
    {
        [JsonPropertyName("total_restarts")] public int TotalRestarts { get; set; }
    }
}
