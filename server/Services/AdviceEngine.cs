using TaoOps.Server.Alerts;
using TaoOps.Server.Settings;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A29 AI 运维建议（PRD v1.4）：本地规则引擎，基于中控既有数据产出可执行建议。
/// 数据经 IAdviceDataSource 只读门面取数，不直接依赖各 Store 内部（架构位评审）。
/// 数据不足输出「数据不足」而非臆测；建议只提示不代做。
/// </summary>
public interface IAdviceDataSource
{
    /// <summary>近 sinceMs 毫秒的监控采样点（升序）。</summary>
    IReadOnlyList<MetricsStore.MetricPoint> GetMetrics(string nodeId, long sinceMs);
    /// <summary>近 sinceMs 毫秒的告警落账（升序）。</summary>
    IReadOnlyList<Alert> GetAlertHistory(string nodeId, long sinceMs);
    /// <summary>节点证书清单（含到期时间与自动续期配置）。</summary>
    IReadOnlyList<CertStatusInfo> GetCertStatus(string nodeId);
    /// <summary>节点的定时备份策略覆盖数。</summary>
    int GetBackupCoverage(string nodeId);
    /// <summary>容器 24h 内重启次数（docker inspect RestartCount）；-1 表示数据不足。</summary>
    Task<int> GetContainerRestarts(string nodeId, CancellationToken ct = default);
}

/// <summary>证书状态摘要（建议引擎视图）。</summary>
public sealed record CertStatusInfo(string Name, DateTimeOffset ExpiresAt, bool AutoRenewConfigured);

public class AdviceEngine(IAdviceDataSource source)
{
    /// <summary>建议级别。</summary>
    public enum AdviceLevel { Info = 0, Warn = 1, Critical = 2 }

    public sealed record Advice(
        string Id, string Level, string Title, string Basis, string? ActionLink, string? ActionText);

    private const long DayMs = 86_400_000L;

    /// <summary>对单节点出建议清单。规则见 PRD §一（6 类），阈值复用系统设置页告警阈值。</summary>
    public async Task<IReadOnlyList<Advice>> AdviseAsync(
        string nodeId, AlertThresholdSettings thresholds, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var advices = new List<Advice>();

        // 规则 1：CPU/内存 24h 峰值超阈值（FR-A23 环形缓冲 + 阈值口径）。
        var metrics = source.GetMetrics(nodeId, now - DayMs);
        if (metrics.Count >= 10)
        {
            var cpuPeak = metrics.Max(m => m.CpuUsedPct);
            if (cpuPeak >= thresholds.CpuPercent)
            {
                advices.Add(new Advice("cpu-peak", LevelName(cpuPeak >= thresholds.CpuPercent + 10 ? AdviceLevel.Critical : AdviceLevel.Warn),
                    $"CPU 24 小时内峰值达 {cpuPeak:F0}%（阈值 {thresholds.CpuPercent}%）",
                    $"近 24 小时 {metrics.Count} 个采样点中 CPU 峰值 {cpuPeak:F0}%，建议排查进程占用（见监控页进程 Top10）。",
                    "/metrics", "查看实时监控"));
            }
            var memPeak = metrics.Max(m => m.MemTotalKB > 0 ? (double)m.MemUsedKB / m.MemTotalKB * 100 : 0);
            if (memPeak >= thresholds.MemPercent)
            {
                advices.Add(new Advice("mem-peak", LevelName(memPeak >= thresholds.MemPercent + 10 ? AdviceLevel.Critical : AdviceLevel.Warn),
                    $"内存 24 小时内峰值达 {memPeak:F0}%（阈值 {thresholds.MemPercent}%）",
                    $"近 24 小时 {metrics.Count} 个采样点中内存峰值 {memPeak:F0}%，建议排查常驻进程或考虑扩容。",
                    "/metrics", "查看实时监控"));
            }

            // 规则 2：磁盘水位预测（近 24h 用量斜率外推；采样不足则降级为快照口径）。
            var withDisk = metrics.Where(m => m.DiskTotalKB > 0).ToList();
            if (withDisk.Count >= 10)
            {
                var latest = withDisk[^1];
                var usedPct = (double)latest.DiskUsedKB / latest.DiskTotalKB * 100;
                double? predictedDays = null;
                if (withDisk.Count >= 20)
                {
                    var spanDays = (latest.Ts - withDisk[0].Ts) / (double)DayMs;
                    if (spanDays > 0.2)
                    {
                        var slopeKbPerDay = (latest.DiskUsedKB - withDisk[0].DiskUsedKB) / spanDays;
                        if (slopeKbPerDay > 0)
                        {
                            var remainKb = (double)latest.DiskTotalKB * 0.9 - latest.DiskUsedKB;
                            predictedDays = remainKb > 0 ? remainKb / slopeKbPerDay : 0;
                        }
                    }
                }
                if (predictedDays is <= 30 || usedPct >= thresholds.DiskCriticalPct)
                {
                    var basis = predictedDays is not null
                        ? $"当前磁盘用量 {usedPct:F0}%，按近 24 小时增速预计 {Math.Ceiling(predictedDays.Value)} 天内超过 90%。"
                        : $"当前磁盘用量 {usedPct:F0}%（超过阈值 {thresholds.DiskCriticalPct}%），采样数据尚不足以预测增速。";
                    advices.Add(new Advice("disk-watermark", LevelName(usedPct >= 90 ? AdviceLevel.Critical : AdviceLevel.Warn),
                        "磁盘空间即将耗尽，建议清理",
                        basis + " 可在「日志清理」页预估可释放空间。",
                        "/logclean", "去日志清理"));
                }
            }
        }

        // 规则 3：证书 30 天内到期（FR-A21 巡检数据）。
        foreach (var cert in source.GetCertStatus(nodeId))
        {
            var daysLeft = (cert.ExpiresAt - DateTimeOffset.UtcNow).TotalDays;
            if (daysLeft <= 30)
            {
                var autoNote = cert.AutoRenewConfigured
                    ? "自动续期已配置，请确认最近一次续期是否成功。"
                    : "该证书未配置自动续期。";
                advices.Add(new Advice($"cert-{cert.Name}", LevelName(daysLeft <= 7 ? AdviceLevel.Critical : AdviceLevel.Warn),
                    $"证书 {cert.Name} 将于 {Math.Ceiling(daysLeft)} 天后到期",
                    $"证书 {cert.Name} 剩余 {Math.Ceiling(daysLeft)} 天。{autoNote}",
                    "/ssl", "去证书管理"));
            }
        }

        // 规则 4：节点无定时备份策略覆盖（FR-A26）。
        if (source.GetBackupCoverage(nodeId) == 0)
        {
            advices.Add(new Advice("no-backup", LevelName(AdviceLevel.Warn),
                "该节点尚无定时备份策略",
                "节点当前没有任何定时备份策略覆盖，故障后可能无法恢复数据。建议在备份页配置每日自动备份。",
                "/backup", "去配置备份"));
        }

        // 规则 5：容器重启计数 ≥3（docker inspect RestartCount，经 docker.manage 通道）。
        var restarts = await source.GetContainerRestarts(nodeId, ct);
        if (restarts >= 3)
        {
            advices.Add(new Advice("container-restarts", LevelName(restarts >= 10 ? AdviceLevel.Critical : AdviceLevel.Warn),
                $"容器 24 小时内重启 {restarts} 次",
                $"节点上容器累计重启计数达 {restarts}，说明有容器反复异常退出。建议查看容器日志定位根因。",
                "/docker", "查看容器"));
        }

        // 规则 6：告警历史 7 天内同源告警 ≥5 次（FR-A19 落账）。
        var alerts = source.GetAlertHistory(nodeId, now - 7 * DayMs);
        if (alerts.Count >= 5)
        {
            var bySource = alerts.GroupBy(a => a.Source).OrderByDescending(g => g.Count()).First();
            if (bySource.Count() >= 5)
            {
                advices.Add(new Advice("alert-storm", LevelName(AdviceLevel.Warn),
                    $"告警「{bySource.Key}」7 天内重复 {bySource.Count()} 次",
                    $"同一来源的告警近 7 天触发 {bySource.Count()} 次，属于反复出现的问题，建议根治而非反复处理。",
                    "/alerts", "查看告警中心"));
            }
        }

        return advices;
    }

    private static string LevelName(AdviceLevel level) => level switch
    {
        AdviceLevel.Critical => "critical",
        AdviceLevel.Warn => "warn",
        _ => "info",
    };
}
