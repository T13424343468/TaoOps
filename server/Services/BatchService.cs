using System.Collections.Concurrent;
using TaoOps.Server.Audit;
using TaoOps.Server.Grpc;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A30 多机批量下发（PRD v1.4）：对多个在线节点并行下发同一只读操作，聚合逐节点结果。
/// 白名单逐 op 硬编码枚举（纯只读，PRD §五）；并发 ≤5；单节点 30s 超时不拖垮整批；
/// 限频每用户每分钟 ≤6 批；节点归属服务端二次校验（离线/不存在拒绝整批）；
/// 聚合详情单节点截断 ≤64KB；审计主记录 + 逐节点子记录进哈希链。
/// </summary>
public class BatchService
{
    /// <summary>批量白名单：module → 允许的 op 集合（args[0]）。module 本身无 op 的填 null。</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>?> Whitelist =
        new Dictionary<string, IReadOnlySet<string>?>
        {
            ["sysinfo.overview"] = null,
            ["sysinfo.metrics_sample"] = null,
            ["sysinfo.process_top"] = null,
            ["cron.manage"] = new HashSet<string> { "list" },
            ["docker.manage"] = new HashSet<string> { "list", "image_list" },
            ["logclean.manage"] = new HashSet<string> { "list", "preview" },
            ["ssl.manage"] = new HashSet<string> { "list" },
        };

    private const int MaxNodes = 20;
    private const int MaxConcurrent = 5;
    private const int PerNodeTimeoutMs = 30_000;
    private const int MaxOutputBytes = 64 * 1024;
    private const int MaxBatchesPerMinute = 6;

    private readonly NodeCommandBridge _bridge;
    private readonly AuditStore _audit;
    private readonly ConcurrentDictionary<string, List<long>> _recentBatches = new();

    public BatchService(NodeCommandBridge bridge, AuditStore audit)
    {
        _bridge = bridge;
        _audit = audit;
    }

    public sealed record BatchNodeResult(
        string NodeId, string Status, int ExitCode, string Output, string RequestId);

    public sealed record BatchResult(
        string BatchId, string Module, int SuccessCount, int FailureCount,
        IReadOnlyList<BatchNodeResult> Nodes);

    public sealed class BatchException(int statusCode, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }

    /// <summary>校验白名单：module 必须在册，含 op 的 module 其 args[0] 必须在允许集合内。</summary>
    public static void ValidateOp(string module, IReadOnlyList<string> args)
    {
        if (!Whitelist.TryGetValue(module, out var ops))
            throw new BatchException(400, $"module not allowed for batch: {module}");
        if (ops is null)
            return;
        var op = args.Count > 0 ? args[0] : "";
        if (!ops.Contains(op))
            throw new BatchException(400, $"op not allowed for batch: {module} {op}");
    }

    /// <summary>限频：每用户每分钟 ≤6 批（滑动窗口，内存态不持久化）。</summary>
    private void EnforceRateLimit(string user)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var window = _recentBatches.GetOrAdd(user, _ => new List<long>());
        lock (window)
        {
            window.RemoveAll(t => now - t > 60_000);
            if (window.Count >= MaxBatchesPerMinute)
                throw new BatchException(429, "batch rate limit exceeded (6/min per user)");
            window.Add(now);
        }
    }

    public async Task<BatchResult> RunAsync(
        string module, IReadOnlyList<string> args, IReadOnlyList<string> nodeIds,
        string user, string role, string ip, CancellationToken ct = default)
    {
        ValidateOp(module, args);
        EnforceRateLimit(user);

        if (nodeIds.Count == 0)
            throw new BatchException(400, "node_ids is empty");
        if (nodeIds.Count > MaxNodes)
            throw new BatchException(400, $"node_ids exceeds max {MaxNodes}");
        if (nodeIds.Distinct().Count() != nodeIds.Count)
            throw new BatchException(400, "node_ids contains duplicates");

        // 节点归属二次校验：不存在或命令通道离线 → 拒绝整批（避免「以为发了 10 台实际发了 8 台」）。
        var connected = new HashSet<string>(
            _bridge.ListNodes().Where(n => n.CommandConnected).Select(n => n.NodeId));
        var missing = nodeIds.Where(id => !connected.Contains(id)).ToList();
        if (missing.Count > 0)
            throw new BatchException(400, $"nodes offline or unknown: {string.Join(",", missing)}");

        var batchId = Guid.NewGuid().ToString("N");
        var results = new ConcurrentBag<BatchNodeResult>();

        using var throttle = new SemaphoreSlim(MaxConcurrent);
        var tasks = nodeIds.Select(async nodeId =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var outcome = await _bridge.DispatchAsync(
                    nodeId, module, args, PerNodeTimeoutMs, user, role, ip, ct);
                results.Add(new BatchNodeResult(
                    nodeId, "success", outcome.ExitCode, Truncate(outcome.Output), outcome.RequestId));
            }
            catch (BridgeException ex) when (ex.StatusCode is 504 or 503)
            {
                // 批量发起后节点掉线/超时：记超时/离线，不重试不阻塞整批（PRD §五）。
                results.Add(new BatchNodeResult(nodeId, "timeout", -1, Truncate(ex.Message), ""));
            }
            catch (BridgeException ex)
            {
                results.Add(new BatchNodeResult(nodeId, "error", -1, Truncate(ex.Message), ""));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                results.Add(new BatchNodeResult(nodeId, "timeout", -1, "batch cancelled", ""));
            }
        });
        await Task.WhenAll(tasks);

        var list = results.OrderBy(r => r.NodeId, StringComparer.Ordinal).ToList();
        var success = list.Count(r => r.Status == "success");

        // 审计：主记录 + 逐节点子记录进哈希链（PRD §二）。
        _audit.Append(new AuditEvent
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            User = user, Role = role, Action = "batch.run", Target = module, Ip = ip,
            Result = $"batch={batchId} nodes={nodeIds.Count} success={success} failed={list.Count - success}",
        });
        foreach (var r in list)
        {
            _audit.Append(new AuditEvent
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                User = user, Role = role, Action = "batch.node", Target = $"{module}@{r.NodeId}", Ip = ip,
                Result = $"batch={batchId} status={r.Status} exit={r.ExitCode} request_id={r.RequestId}",
            });
        }

        return new BatchResult(batchId, module, success, list.Count - success, list);
    }

    internal static string Truncate(string output)
    {
        if (output.Length <= MaxOutputBytes)
            return output;
        return output[..MaxOutputBytes] + "\n...[truncated]";
    }
}
