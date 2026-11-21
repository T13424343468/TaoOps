using Grpc.Core;
using Microsoft.Extensions.Logging;
using TaoOps.Server.Grpc;

namespace TaoOps.Server.Services;

/// <summary>
/// AgentService gRPC 实现。契约正源：src/docs/protocol-contract.md。
/// Dispatch 校验后经 CommandStream 转发到目标节点，由 Agent 侧统一分发器执行。
/// </summary>
public class AgentService : Grpc.AgentService.AgentServiceBase
{
    // 契约常量（protocol-contract.md §1/§4）
    internal static readonly string[] ShellWhitelist = { "echo", "uname", "df", "free", "uptime" };
    internal static readonly string[] ModuleWhitelist =
    {
        "sysinfo.overview", "site.manage", "db.manage", "files.manage", "term.manage",
        "cron.manage", "env.manage", "fw.manage", "backup.manage",
    };
    private static readonly char[] ForbiddenChars = { ';', '|', '&', '`', '$' };

    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 300_000;

    private readonly NodeRegistry _registry;
    private readonly NodeCommandHub _hub;
    private readonly ILogger<AgentService> _logger;

    public AgentService(NodeRegistry registry, NodeCommandHub hub, ILogger<AgentService> logger)
    {
        _registry = registry;
        _hub = hub;
        _logger = logger;
    }

    public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent_id is required"));

        var node = _registry.Register(request);
        _logger.LogInformation("Agent registered: agent_id={AgentId} node_id={NodeId} host={Host}",
            request.AgentId, node.NodeId, request.Hostname);

        return Task.FromResult(new RegisterResponse
        {
            NodeId = node.NodeId,
            Config = new NodeConfig { HeartbeatIntervalMs = 15_000 },
        });
    }

    public override async Task Heartbeat(
        IAsyncStreamReader<HeartbeatMsg> requestStream,
        IServerStreamWriter<HeartbeatAck> responseStream,
        ServerCallContext context)
    {
        while (await requestStream.MoveNext(context.CancellationToken))
        {
            var msg = requestStream.Current;
            if (!_registry.Touch(msg.NodeId))
                throw new RpcException(new Status(StatusCode.NotFound, $"unknown node_id {msg.NodeId}"));

            await responseStream.WriteAsync(new HeartbeatAck { Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                context.CancellationToken);
        }
    }

    public override async Task<DispatchResult> Dispatch(DispatchRequest request, ServerCallContext context)
    {
        var node = _registry.GetByNodeId(request.NodeId);
        if (node is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"unknown node_id {request.NodeId}"));

        ValidateCommand(request);

        var timeoutMs = request.TimeoutMs is 0 ? DefaultTimeoutMs : request.TimeoutMs;
        if (timeoutMs < 0 || timeoutMs > MaxTimeoutMs)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"timeout_ms out of range [0, {MaxTimeoutMs}]"));

        if (string.IsNullOrEmpty(request.RequestId))
            request.RequestId = Guid.NewGuid().ToString("N");

        var result = await _hub.DispatchAsync(
            request.NodeId, request, TimeSpan.FromMilliseconds(timeoutMs), context.CancellationToken);
        _logger.LogInformation("Dispatch {Command} to {NodeId} -> exit {ExitCode}",
            request.Command, request.NodeId, result.ExitCode);
        return result;
    }

    /// <summary>Agent 命令流：请求头 x-node-id 标识节点；流断开即摘除通道。</summary>
    public override Task CommandStream(
        IAsyncStreamReader<DispatchResult> requestStream,
        IServerStreamWriter<DispatchRequest> responseStream,
        ServerCallContext context)
    {
        var nodeId = context.RequestHeaders.GetValue("x-node-id");
        if (string.IsNullOrWhiteSpace(nodeId) || _registry.GetByNodeId(nodeId) is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"unknown or missing x-node-id"));

        _logger.LogInformation("Command stream attached: node_id={NodeId}", nodeId);
        return _hub.AttachAsync(nodeId, CommandStreamAdapter.Writer(responseStream),
            CommandStreamAdapter.Reader(requestStream), context.CancellationToken);
    }

    private static void ValidateCommand(DispatchRequest request)
    {
        var isShell = Array.Exists(ShellWhitelist, c => c == request.Command);
        var isModule = Array.Exists(ModuleWhitelist, c => c == request.Command);
        if (!isShell && !isModule)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"command not allowed: {request.Command}"));

        // Shell 命令参数禁止注入字符（契约 §4）；模块命令参数为 key=value，不做 shell 解释。
        if (isShell && request.Args.ToArray().Any(a => a.IndexOfAny(ForbiddenChars) >= 0))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "args contain forbidden characters"));
    }
}
