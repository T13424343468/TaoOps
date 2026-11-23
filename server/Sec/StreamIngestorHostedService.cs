using TaoOps.Server.Services;
using TaoOps.Server.Waf;

namespace TaoOps.Server.Sec;

/// <summary>
/// 事件回流自动订阅托管服务（断言 A4）：节点上线后自动为 WAF 与 SEC 日志流建立订阅，
/// 免手工触发。Agent 日志通道尚未挂接时静默重试（下轮巡检补订），不崩不丢账；
/// 订阅任务半路故障（流断开）同样移出已订集合等待重订。WAF 与 SEC 两路独立跟踪，
/// 一路失败不拖累另一路。
/// </summary>
public sealed class StreamIngestorHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly NodeRegistry _registry;
    private readonly Func<string, CancellationToken, Task> _wafStart;
    private readonly Func<string, CancellationToken, Task> _secStart;
    private readonly ILogger<StreamIngestorHostedService>? _logger;
    private readonly TimeSpan _pollInterval;

    // nodeId → 订阅任务；任务故障（流断开）后移出等待重订。
    private readonly Dictionary<string, Task> _wafTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _secTasks = new(StringComparer.Ordinal);

    public StreamIngestorHostedService(NodeRegistry registry, WafEventIngestor waf,
        SecEventIngestor sec, ILogger<StreamIngestorHostedService> logger)
        : this(registry, (node, ct) => waf.StartAsync(node, ct), (node, ct) => sec.StartAsync(node, ct), logger)
    { }

    internal StreamIngestorHostedService(NodeRegistry registry,
        Func<string, CancellationToken, Task> wafStart, Func<string, CancellationToken, Task> secStart,
        ILogger<StreamIngestorHostedService>? logger = null)
    {
        _registry = registry;
        _wafStart = wafStart;
        _secStart = secStart;
        _logger = logger;
        var secs = int.TryParse(Environment.GetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS"), out var s) && s > 0
            ? s : 5;
        _pollInterval = TimeSpan.FromSeconds(secs);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 巡检自身异常不终止托管服务（空吞禁令：留痕不静默）。
                _logger?.LogWarning(ex, "ingestor poll failed");
            }
            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>对每个已知节点补订未就绪通道；返回本轮新建立订阅数（测试口径）。</summary>
    internal async Task<int> PollOnceAsync(CancellationToken ct)
    {
        PruneFaulted(_wafTasks, "waf");
        PruneFaulted(_secTasks, "sec");

        var started = 0;
        foreach (var node in _registry.All())
        {
            if (!_wafTasks.ContainsKey(node.NodeId))
            {
                try
                {
                    _wafTasks[node.NodeId] = _wafStart(node.NodeId, ct);
                    started++;
                }
                catch (InvalidOperationException)
                {
                    // 日志流未挂（Agent 未连或通道未就绪），下轮重试。
                }
            }
            if (!_secTasks.ContainsKey(node.NodeId))
            {
                try
                {
                    _secTasks[node.NodeId] = _secStart(node.NodeId, ct);
                    started++;
                }
                catch (InvalidOperationException)
                {
                    // 同上。
                }
            }
        }
        await Task.CompletedTask;
        return started;
    }

    private void PruneFaulted(Dictionary<string, Task> tasks, string channel)
    {
        List<string>? faulted = null;
        foreach (var (nodeId, task) in tasks)
        {
            if (!task.IsFaulted) continue;
            (faulted ??= []).Add(nodeId);
            var ex = task.Exception?.GetBaseException();
            _logger?.LogWarning(ex, "{Channel} ingest subscription for node {NodeId} faulted; will resubscribe",
                channel, nodeId);
        }
        if (faulted is not null)
            foreach (var id in faulted)
                tasks.Remove(id);
    }
}
