using System.Collections.Concurrent;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A23 资源实时监控内存环形缓冲：每节点保留最近 24h×60s = 1440 个采样点，
/// 超限淘汰最旧；不落盘，重启清零（PRD 口径）。线程安全。
/// </summary>
public sealed class MetricsStore
{
    /// <summary>单节点最大保留点数（24h × 60s）。</summary>
    public const int Capacity = 1440;

    /// <summary>一个采样点（时间戳为 Unix 毫秒；磁盘用量字段 FR-A29，0 表示数据不足）。</summary>
    public sealed record MetricPoint(long Ts, double CpuUsedPct, ulong MemTotalKB, ulong MemUsedKB,
        ulong DiskReadBps, ulong DiskWriteBps, ulong NetRxTotal, ulong NetTxTotal,
        ulong DiskUsedKB = 0, ulong DiskTotalKB = 0);

    private readonly ConcurrentDictionary<string, Queue<MetricPoint>> _buffers = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>追加一个采样点；超限淘汰最旧。未知节点惰性建队列。</summary>
    public void Add(string nodeId, MetricPoint point)
    {
        var q = _buffers.GetOrAdd(nodeId, _ => new Queue<MetricPoint>());
        lock (_lock)
        {
            q.Enqueue(point);
            while (q.Count > Capacity) q.Dequeue();
        }
    }

    /// <summary>查询某节点自 sinceMs 起的点（按时间升序）；未知节点返回空。</summary>
    public IReadOnlyList<MetricPoint> Query(string nodeId, long sinceMs = 0)
    {
        if (!_buffers.TryGetValue(nodeId, out var q)) return Array.Empty<MetricPoint>();
        lock (_lock)
        {
            return q.Where(p => p.Ts >= sinceMs).ToArray();
        }
    }

    /// <summary>清空指定节点缓冲（测试用）。</summary>
    public void Clear(string nodeId)
    {
        if (_buffers.TryRemove(nodeId, out var q))
        {
            lock (_lock) { q.Clear(); }
        }
    }
}
