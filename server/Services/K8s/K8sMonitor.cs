using System.Collections.Concurrent;
using k8s;

namespace TaoOps.Server.Services.K8s;

/// <summary>
/// FR-A28 K8s 巡检（中控侧）：每 60s 对全部集群拉取全命名空间 Pod，
/// CrashLoopBackOff / ImagePullBackOff / 重启次数激增（≥5）产生 k8s 级别
/// warn 告警（接 FR-A19，边缘触发，恢复复位）。巡检失败静默（监控自身
/// 降级不产生告警噪音，口径同 FR-A23）。
/// </summary>
public sealed class K8sMonitor(
    KubeClusterStore store,
    IK8sClientFactory factory,
    Alerts.AlertCenter alerts,
    ILogger<K8sMonitor> logger) : BackgroundService
{
    /// <summary>巡检周期（PRD 口径 60s；测试可注入更短）。</summary>
    public static readonly int IntervalMs = 60_000;
    /// <summary>重启次数激增阈值（PRD 口径 ≥5 次/10min，简化为累计 ≥5 触发）。</summary>
    public const int RestartSpikeThreshold = 5;

    private readonly ConcurrentDictionary<string, bool> _alerted = new(StringComparer.Ordinal);

    /// <summary>
    /// Pod 健康判定（纯函数，测试直测）：CrashLoopBackOff / ImagePullBackOff /
    /// 重启次数 ≥ 阈值视为不健康，返回（命名空间、Pod 名、原因）清单。
    /// </summary>
    public static IReadOnlyList<(string Ns, string Pod, string Reason)> FindUnhealthy(
        IEnumerable<k8s.Models.V1Pod> pods)
    {
        var result = new List<(string, string, string)>();
        foreach (var pod in pods)
        {
            var waiting = pod.Status?.ContainerStatuses?
                .FirstOrDefault(c => c.State?.Waiting is not null)?.State?.Waiting?.Reason;
            var restarts = (int)(pod.Status?.ContainerStatuses?.Sum(c => c.RestartCount) ?? 0);
            if (waiting is "CrashLoopBackOff" or "ImagePullBackOff")
                result.Add((pod.Metadata.NamespaceProperty ?? "", pod.Metadata.Name, waiting));
            else if (restarts >= RestartSpikeThreshold)
                result.Add((pod.Metadata.NamespaceProperty ?? "", pod.Metadata.Name, $"restarts={restarts}"));
        }
        return result;
    }

    /// <summary>单轮巡检（测试可直调）：返回本轮产生的告警条数。</summary>
    public int ScanOnce(long? tsMs = null)
    {
        int raised = 0;
        foreach (var cluster in store.List())
        {
            try
            {
                using var client = factory.Create(store.Load(cluster.Name)!);
                // 客户端无 Pod 全命名空间列表方法：逐命名空间聚合（口径同 K8sClusterService）。
                var pods = new List<k8s.Models.V1Pod>();
                foreach (var nsItem in client.CoreV1.ListNamespace().Items)
                    pods.AddRange(client.CoreV1.ListNamespacedPod(nsItem.Metadata.Name).Items);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (ns, podName, reason) in FindUnhealthy(pods))
                {
                    var key = $"{cluster.Name}/{ns}/{podName}";
                    seen.Add(key);
                    if (_alerted.TryGetValue(key, out var already) && already) continue;
                    _alerted[key] = true;
                    alerts.Raise("k8s", Alerts.AlertLevels.Warn, cluster.Name, $"{ns}/{podName}: {reason}", tsMs);
                    raised++;
                }
                // 恢复复位：本轮健康（或消失）的 Pod 清除边缘标记。
                foreach (var key in _alerted.Keys.Where(k => k.StartsWith($"{cluster.Name}/", StringComparison.Ordinal)
                             && !seen.Contains(k)).ToList())
                    _alerted.TryRemove(key, out _);
            }
            catch (Exception ex)
            {
                // 巡检失败静默：集群不可达不产生告警噪音，仅记调试日志。
                logger.LogDebug(ex, "k8s scan failed for cluster {Cluster}", cluster.Name);
            }
        }
        return raised;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { ScanOnce(); }
            catch (Exception ex) { logger.LogDebug(ex, "k8s monitor scan error"); }
            await Task.Delay(IntervalMs, stoppingToken);
        }
    }
}
