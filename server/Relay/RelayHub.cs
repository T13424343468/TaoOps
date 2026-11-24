using System.Collections.Concurrent;
using Grpc.Core;
using TaoOps.Server.Alerts;
using TaoOps.Server.Audit;
using TaoOps.Server.Grpc;
using TaoOps.Server.Services;

namespace TaoOps.Server.Relay;

/// <summary>
/// 节点间备份中转枢纽（FR-A13，契约 §7）：源端 Agent 推流分片，中控只过账
/// 不落盘、流式转发到目标节点。单目标节点并发转发数=1；单分片顺序转发，
/// 在途内存占用 = 单分片（4MB）≤ 64MB 上限。中转流量按 relay 记账，面板可查。
/// </summary>
public class RelayHub
{
    private const int RelayChunkTimeoutMs = 120_000;

    private readonly NodeCommandHub _hub;
    private readonly AlertCenter? _alerts;
    private readonly AuditStore? _audit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targetGates = new();
    private readonly ConcurrentDictionary<string, RelayStat> _relays = new();
    private long _totalRelayedBytes;

    /// <summary>单条中转会话的流量统计视图。</summary>
    public sealed record RelayStat(
        string RelayId, string TargetNodeId, long RelayedBytes, long StartedMs, string Status);

    public RelayHub(NodeCommandHub hub, AlertCenter? alerts = null, AuditStore? audit = null)
    {
        _hub = hub;
        _alerts = alerts;
        _audit = audit;
    }

    /// <summary>BackupRelay 双向流主体：逐分片转发并回执；nack 后源端重传或重跑续传。</summary>
    public async Task HandleStream(
        IAsyncStreamReader<RelayChunk> requestStream,
        IServerStreamWriter<RelayAck> responseStream,
        ServerCallContext context)
    {
        while (await requestStream.MoveNext(context.CancellationToken))
        {
            var chunk = requestStream.Current;
            var (ok, error) = await RelayOneAsync(chunk, context.CancellationToken);
            await responseStream.WriteAsync(new RelayAck
            {
                ChunkIndex = chunk.ChunkIndex,
                Ok = ok,
                Error = error ?? "",
                RelayedBytes = Interlocked.Read(ref _totalRelayedBytes),
            }, context.CancellationToken);
            if (!ok)
            {
                // 首个失败即断流：源端重跑 send 续传（目标端已收分片幂等跳过）。
                return;
            }
        }
    }

    private async Task<(bool Ok, string? Error)> RelayOneAsync(RelayChunk chunk, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chunk.TargetNodeId) || string.IsNullOrWhiteSpace(chunk.UploadId))
            return (false, "relay: target_node_id and upload_id are required");

        if (!_hub.IsConnected(chunk.TargetNodeId))
        {
            RaiseBackupAlert(chunk.TargetNodeId, $"relay {chunk.UploadId}: target node not connected");
            return (false, $"relay: target node {chunk.TargetNodeId} not connected");
        }

        TrackRelay(chunk);

        // 首分片先 relay_begin：目标端磁盘预检（剩余空间 < 声明大小即拒收并告警）。
        if (chunk.ChunkIndex == 0)
        {
            var beginArgs = new[]
            {
                "op=relay_begin",
                $"upload_id={chunk.UploadId}",
                $"name={chunk.Name}",
                $"total_size={chunk.TotalSize}",
                $"chunk_count={chunk.ChunkCount}",
                $"file_sha256={chunk.FileSha256}",
                $"source={chunk.Source}",
            };
            var (ok, err) = await DispatchToTargetAsync(chunk.TargetNodeId, chunk.UploadId, beginArgs, null, ct);
            if (!ok)
            {
                MarkRelay(chunk.UploadId, "failed");
                RaiseBackupAlert(chunk.TargetNodeId, $"relay {chunk.UploadId}: disk precheck rejected: {err}");
                return (false, $"relay: begin rejected: {err}");
            }
        }

        // 数据分片：分片级 SHA-256 由目标端校验，失败 nack 由源端重传该分片。
        if (!chunk.Eof || chunk.Data.Length > 0)
        {
            var putArgs = new[]
            {
                "op=relay_put",
                $"upload_id={chunk.UploadId}",
                $"chunk_index={chunk.ChunkIndex}",
                $"chunk_sha256={chunk.ChunkSha256}",
            };
            var gate = _targetGates.GetOrAdd(chunk.TargetNodeId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            bool ok;
            string? err;
            try
            {
                (ok, err) = await DispatchToTargetAsync(chunk.TargetNodeId, chunk.UploadId, putArgs, chunk.Data.ToByteArray(), ct);
            }
            finally
            {
                gate.Release();
            }
            if (!ok)
            {
                MarkRelay(chunk.UploadId, "failed");
                return (false, $"relay: chunk {chunk.ChunkIndex} rejected: {err}");
            }
            Interlocked.Add(ref _totalRelayedBytes, chunk.Data.Length);
            AddRelayBytes(chunk.UploadId, chunk.Data.Length);
        }

        // 末分片：整包 SHA-256 最终确认；失败清理装配会话并告警。
        if (chunk.Eof)
        {
            var assembleArgs = new[] { "op=relay_assemble", $"upload_id={chunk.UploadId}" };
            var (ok, err) = await DispatchToTargetAsync(chunk.TargetNodeId, chunk.UploadId, assembleArgs, null, ct);
            if (!ok)
            {
                MarkRelay(chunk.UploadId, "failed");
                RaiseBackupAlert(chunk.TargetNodeId, $"relay {chunk.UploadId}: whole-archive verification failed: {err}");
                return (false, $"relay: assemble failed: {err}");
            }
            MarkRelay(chunk.UploadId, "done");
        }
        return (true, null);
    }

    private async Task<(bool Ok, string? Error)> DispatchToTargetAsync(
        string nodeId, string uploadId, string[] args, byte[]? blob, CancellationToken ct)
    {
        var request = new DispatchRequest
        {
            NodeId = nodeId,
            Command = "backup.manage",
            RequestId = Guid.NewGuid().ToString("N"),
            TimeoutMs = RelayChunkTimeoutMs,
        };
        request.Args.AddRange(args);
        if (blob is { Length: > 0 })
            request.Blob = Google.Protobuf.ByteString.CopyFrom(blob);

        string auditResult = "error";
        try
        {
            var dispatched = await _hub.DispatchAsync(nodeId, request, TimeSpan.FromMilliseconds(RelayChunkTimeoutMs), ct);
            auditResult = dispatched.ExitCode.ToString();
            if (dispatched.ExitCode != 0)
                return (false, dispatched.Output);
            return (true, null);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            auditResult = "unavailable";
            return (false, "target command channel not connected");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            auditResult = "timeout";
            return (false, $"dispatch timed out after {RelayChunkTimeoutMs}ms");
        }
        finally
        {
            _audit?.Append(new AuditEvent
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                User = "relay",
                Role = "System",
                Action = "node.cmd",
                Target = $"/relay/{uploadId}",
                Ip = "",
                Result = auditResult,
            });
        }
    }

    private void TrackRelay(RelayChunk chunk)
    {
        _relays.GetOrAdd(chunk.UploadId, _ => new RelayStat(
            chunk.UploadId, chunk.TargetNodeId, 0,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "active"));
    }

    private void AddRelayBytes(string relayId, long bytes)
    {
        if (_relays.TryGetValue(relayId, out var stat))
            _relays[relayId] = stat with { RelayedBytes = stat.RelayedBytes + bytes };
    }

    private void MarkRelay(string relayId, string status)
    {
        if (_relays.TryGetValue(relayId, out var stat))
            _relays[relayId] = stat with { Status = status };
    }

    private void RaiseBackupAlert(string nodeId, string detail) =>
        _alerts?.Raise("backup", "warn", nodeId, detail);

    /// <summary>中转流量统计（面板 GET /api/relay/stats）。</summary>
    public object Stats() => new
    {
        total_relayed_bytes = Interlocked.Read(ref _totalRelayedBytes),
        active_relays = _relays.Values.Count(r => r.Status == "active"),
        relays = _relays.Values
            .OrderByDescending(r => r.StartedMs)
            .Select(r => new
            {
                relay_id = r.RelayId,
                target_node_id = r.TargetNodeId,
                relayed_bytes = r.RelayedBytes,
                started_ms = r.StartedMs,
                status = r.Status,
            })
            .ToArray(),
    };
}
