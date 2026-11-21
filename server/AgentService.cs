using Grpc.Core;
using Microsoft.Extensions.Logging;
using TaoOps.Server.Grpc;

namespace TaoOps.Server.Services;

/// <summary>
/// AgentService gRPC 实现。契约正源：src/docs/protocol-contract.md。
/// </summary>
public class AgentService : Grpc.AgentService.AgentServiceBase
{
    // 契约常量（protocol-contract.md §1/§4）
    internal static readonly string[] AllowedCommands = { "echo", "uname", "df", "free", "uptime" };
    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 300_000;

    private readonly NodeRegistry _registry;
    private readonly ILogger<AgentService> _logger;

    public AgentService(NodeRegistry registry, ILogger<AgentService> logger)
    {
        _registry = registry;
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

    public override Task<DispatchResult> Dispatch(DispatchRequest request, ServerCallContext context)
    {
        var node = _registry.GetByNodeId(request.NodeId);
        if (node is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"unknown node_id {request.NodeId}"));

        if (!AllowedCommands.Contains(request.Command))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"command not allowed: {request.Command}"));

        var timeoutMs = request.TimeoutMs is 0 ? DefaultTimeoutMs : request.TimeoutMs;
        if (timeoutMs < 0 || timeoutMs > MaxTimeoutMs)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"timeout_ms out of range [0, {MaxTimeoutMs}]"));

        // M1 本机回环：中控与 Agent 同机时直接执行；跨节点执行走 Agent 通道（M2）。
        var result = LocalExec.Execute(request.Command, request.Args.ToArray(), TimeSpan.FromMilliseconds(timeoutMs));
        return Task.FromResult(new DispatchResult { ExitCode = result.ExitCode, Output = result.Output });
    }
}
