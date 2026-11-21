using Grpc.Core;
using Grpc.Net.Client;
using TaoOps.Agent.Metrics;
using TaoOps.Server.Grpc;

namespace TaoOps.Agent;

/// <summary>
/// Agent 主循环：注册 → 心跳流 → 等待指令。断链指数退避重连（契约 §1）。
/// </summary>
public class AgentWorker
{
    public const int HeartbeatIntervalMs = 15_000;
    private const int ReconnectInitialMs = 1_000;
    private const int ReconnectMaxMs = 60_000;

    private readonly string _serverUrl;
    private readonly string _agentId;
    private readonly Func<MetricsSnapshot> _collect;
    private readonly Action<string> _log;

    public AgentWorker(string serverUrl, string agentId, Func<MetricsSnapshot> collect, Action<string> log)
    {
        _serverUrl = serverUrl;
        _agentId = agentId;
        _collect = collect;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var delay = ReconnectInitialMs;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ct);
                delay = ReconnectInitialMs; // 成功会话后重置退避
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                _log("node unknown, re-registering");
            }
            catch (Exception ex)
            {
                _log($"connection lost: {ex.Message}");
            }
            await Task.Delay(delay, ct);
            delay = Math.Min(delay * 2, ReconnectMaxMs);
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        using var channel = GrpcChannel.ForAddress(_serverUrl);
        var client = new Grpc.AgentService.AgentServiceClient(channel);

        var reg = await client.RegisterAsync(new RegisterRequest
        {
            AgentId = _agentId,
            Hostname = Environment.MachineName,
            Os = SystemInfo.OsName,
            Arch = SystemInfo.Arch,
            Version = SystemInfo.AgentVersion,
        }, cancellationToken: ct);
        _log($"registered as node {reg.NodeId}");

        var interval = reg.Config?.HeartbeatIntervalMs is > 0 ? reg.Config.HeartbeatIntervalMs : HeartbeatIntervalMs;

        using var heartbeat = client.Heartbeat(cancellationToken: ct);
        var heartbeatTask = PumpHeartbeatsAsync(heartbeat, reg.NodeId, interval, ct);

        // M1：本机回环演示——中控 Dispatch 走服务端本地执行；Agent 侧指令通道 M2 接入。
        await heartbeatTask;
    }

    private async Task PumpHeartbeatsAsync(
        AsyncDuplexStreamingCall<HeartbeatMsg, HeartbeatAck> heartbeat,
        string nodeId, int intervalMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var m = _collect();
            await heartbeat.RequestStream.WriteAsync(new HeartbeatMsg
            {
                NodeId = nodeId,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Cpu = m.Cpu,
                Mem = m.Mem,
                Disk = m.Disk,
                Load = m.Load,
            }, ct);
            await Task.Delay(intervalMs, ct);
        }
        await heartbeat.RequestStream.CompleteAsync();
    }
}
