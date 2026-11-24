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
        "cron.manage", "env.manage", "fw.manage", "backup.manage", "ftp.manage",
        "sec.manage", "docker.manage", "webstats.manage", "migrate.manage", "lb.manage",
        "logclean.manage", "ssl.manage",
    };
    private static readonly char[] ForbiddenChars = { ';', '|', '&', '`', '$' };

    private const int DefaultTimeoutMs = 30_000;
    private const int MaxTimeoutMs = 300_000;

    private readonly NodeRegistry _registry;
    private readonly NodeCommandHub _hub;
    private readonly NodeStreamHub _streams;
    private readonly ILogger<AgentService> _logger;
    /// <summary>统一告警中心（FR-A19）：心跳磁盘水位接线；测试直构时可为空。</summary>
    private readonly Alerts.AlertCenter? _alerts;
    /// <summary>节点间备份中转（FR-A13）：源端分片流转发；测试直构时可注入。</summary>
    private readonly Relay.RelayHub _relay;

    public AgentService(NodeRegistry registry, NodeCommandHub hub, NodeStreamHub streams,
        ILogger<AgentService> logger, Alerts.AlertCenter? alerts = null, Relay.RelayHub? relay = null)
    {
        _registry = registry;
        _hub = hub;
        _streams = streams;
        _logger = logger;
        _alerts = alerts;
        _relay = relay ?? new Relay.RelayHub(hub, alerts);
    }

    /// <summary>节点间备份中转（FR-A13，契约 §7）：源端推流分片，中控转发至目标节点。</summary>
    public override Task BackupRelay(
        IAsyncStreamReader<RelayChunk> requestStream,
        IServerStreamWriter<RelayAck> responseStream,
        ServerCallContext context)
        => _relay.HandleStream(requestStream, responseStream, context);

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

            // FR-A19：磁盘水位接线（≥90% 边缘触发 critical，<85% 迟滞复位）。
            _alerts?.CheckDiskWatermark(msg.NodeId, msg.Disk, msg.Ts > 0 ? msg.Ts : null);

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

    /// <summary>Agent 终端流（M2）：会话头 x-session-id；流断开即摘除会话。</summary>
    public override Task TermStream(
        IAsyncStreamReader<TermData> requestStream,
        IServerStreamWriter<TermData> responseStream,
        ServerCallContext context)
    {
        var nodeId = context.RequestHeaders.GetValue("x-node-id");
        if (string.IsNullOrWhiteSpace(nodeId) || _registry.GetByNodeId(nodeId) is null)
            throw new RpcException(new Status(StatusCode.NotFound, "unknown or missing x-node-id"));
        var sessionId = context.RequestHeaders.GetValue("x-session-id");
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "missing x-session-id"));

        _logger.LogInformation("Term stream attached: node_id={NodeId} session={SessionId}", nodeId, sessionId);
        var session = _streams.AttachTerm(nodeId, sessionId);
        return PumpTermAsync(requestStream, responseStream, session, context.CancellationToken)
            .ContinueWith(_ => _streams.DetachTerm(sessionId), TaskScheduler.Default);
    }

    /// <summary>Agent 日志流（M2）：下行订阅控制，上行日志块广播。</summary>
    public override Task LogStream(
        IAsyncStreamReader<LogChunk> requestStream,
        IServerStreamWriter<LogControl> responseStream,
        ServerCallContext context)
    {
        var nodeId = context.RequestHeaders.GetValue("x-node-id");
        if (string.IsNullOrWhiteSpace(nodeId) || _registry.GetByNodeId(nodeId) is null)
            throw new RpcException(new Status(StatusCode.NotFound, "unknown or missing x-node-id"));

        _logger.LogInformation("Log stream attached: node_id={NodeId}", nodeId);
        var session = _streams.AttachLog(nodeId);
        return PumpLogAsync(requestStream, responseStream, session, context.CancellationToken)
            .ContinueWith(_ => _streams.DetachLog(nodeId), TaskScheduler.Default);
    }

    /// <summary>终端双泵：读循环收 Agent 输出进 Inbound，写循环推 Outbound 到 Agent。</summary>
    private static async Task PumpTermAsync(
        IAsyncStreamReader<TermData> requestStream,
        IServerStreamWriter<TermData> responseStream,
        NodeStreamHub.TermSession session,
        CancellationToken ct)
    {
        var readLoop = Task.Run(async () =>
        {
            try
            {
                while (await requestStream.MoveNext(ct))
                    await session.Inbound.Writer.WriteAsync(requestStream.Current, ct);
            }
            catch (OperationCanceledException) { }
            catch (RpcException) { }
            finally
            {
                // 请求流收尾（Agent 断开）即关闭写侧，让下行泵自然退出，不必等外部取消。
                session.Outbound.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in session.Outbound.Reader.ReadAllAsync(ct))
                await responseStream.WriteAsync(item, ct);
        }
        catch (OperationCanceledException) { }
        await readLoop;
    }

    /// <summary>日志双泵：读循环广播 Agent 日志块，写循环推订阅控制。</summary>
    private static async Task PumpLogAsync(
        IAsyncStreamReader<LogChunk> requestStream,
        IServerStreamWriter<LogControl> responseStream,
        NodeStreamHub.LogSession session,
        CancellationToken ct)
    {
        var readLoop = Task.Run(async () =>
        {
            try
            {
                while (await requestStream.MoveNext(ct))
                    session.Broadcast(requestStream.Current);
            }
            catch (OperationCanceledException) { }
            catch (RpcException) { }
            finally
            {
                // 请求流收尾（Agent 断开）即关闭写侧，让下行泵自然退出，不必等外部取消。
                session.Outbound.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var ctrl in session.Outbound.Reader.ReadAllAsync(ct))
                await responseStream.WriteAsync(ctrl, ct);
        }
        catch (OperationCanceledException) { }
        await readLoop;
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
