using Grpc.Core;
using TaoOps.Server.Audit;
using TaoOps.Server.Grpc;

namespace TaoOps.Server.Services;

/// <summary>下发桥错误：带 HTTP 映射状态码（契约 §6）。</summary>
public sealed class BridgeException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// HTTP 下发桥（项5，契约 §6）：面板请求 → NodeCommandHub 下发，
/// 复用 gRPC 同一白名单校验（AgentService 白名单正源），每次下发进 FR-D4 审计哈希链。
/// </summary>
public class NodeCommandBridge
{
    private readonly NodeRegistry _registry;
    private readonly NodeCommandHub _hub;
    private readonly AuditStore _audit;

    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 300_000;
    private static readonly char[] ForbiddenChars = { ';', '|', '&', '`', '$' };

    public NodeCommandBridge(NodeRegistry registry, NodeCommandHub hub, AuditStore audit)
    {
        _registry = registry;
        _hub = hub;
        _audit = audit;
    }

    /// <summary>节点列表视图（契约 §6：含命令通道在连标志）。</summary>
    public sealed record NodeView(
        string NodeId, string AgentId, string Hostname, long LastSeenMs, long RegisteredAtMs, bool CommandConnected);

    /// <summary>下发结果视图。</summary>
    public sealed record CommandOutcome(int ExitCode, string Output, string RequestId);

    /// <summary>列出全部已知节点，按注册时间排序保证列表稳定。</summary>
    public IReadOnlyList<NodeView> ListNodes() =>
        _registry.All()
            .OrderBy(n => n.RegisteredAt)
            .Select(n => new NodeView(
                n.NodeId, n.AgentId, n.Hostname,
                n.LastSeen.ToUnixTimeMilliseconds(), n.RegisteredAt.ToUnixTimeMilliseconds(),
                _hub.IsConnected(n.NodeId)))
            .ToList();

    /// <summary>
    /// 下发指令：校验（白名单/注入字符/超时区间）→ hub 转发 → 审计留痕（成败均进哈希链）。
    /// 校验失败抛 BridgeException（400/404），通道未连 503，超时 504。
    /// </summary>
    public async Task<CommandOutcome> DispatchAsync(
        string nodeId, string? command, IEnumerable<string>? args, int timeoutMs,
        string user, string role, string ip, CancellationToken ct = default)
    {
        if (_registry.GetByNodeId(nodeId) is null)
            throw new BridgeException(404, $"unknown node_id {nodeId}");

        var argList = (args ?? []).ToArray();
        Validate(command, argList);

        var timeout = timeoutMs is 0 ? DefaultTimeoutMs : timeoutMs;
        if (timeout < 0 || timeout > MaxTimeoutMs)
            throw new BridgeException(400, $"timeout_ms out of range [0, {MaxTimeoutMs}]");

        var request = new DispatchRequest
        {
            NodeId = nodeId,
            Command = command ?? "",
            RequestId = Guid.NewGuid().ToString("N"),
        };
        request.Args.AddRange(argList);

        string auditResult = "error";
        try
        {
            var dispatched = await _hub.DispatchAsync(nodeId, request, TimeSpan.FromMilliseconds(timeout), ct);
            auditResult = dispatched.ExitCode.ToString();
            return new CommandOutcome(dispatched.ExitCode, dispatched.Output, request.RequestId);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            auditResult = "unavailable";
            throw new BridgeException(503, "node command channel not connected");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            auditResult = "timeout";
            throw new BridgeException(504, $"dispatch timed out after {timeout}ms");
        }
        finally
        {
            _audit.Append(new AuditEvent
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                User = user,
                Role = role,
                Action = "node.cmd",
                Target = $"/api/nodes/{nodeId}/cmd",
                Ip = ip,
                Result = auditResult,
            });
        }
    }

    // 校验口径与 AgentService.ValidateCommand 一致（契约 §4 单一正源）；白名单直接复用其常量。
    private static void Validate(string? command, string[] args)
    {
        var isShell = Array.Exists(AgentService.ShellWhitelist, c => c == command);
        var isModule = Array.Exists(AgentService.ModuleWhitelist, c => c == command);
        if (!isShell && !isModule)
            throw new BridgeException(400, $"command not allowed: {command}");
        if (isShell && args.Any(a => a.IndexOfAny(ForbiddenChars) >= 0))
            throw new BridgeException(400, "args contain forbidden characters");
    }
}
