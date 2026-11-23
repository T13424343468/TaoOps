using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using TaoOps.Server.Grpc;

namespace TaoOps.Server.Services;

/// <summary>Agent 命令流写出端（gRPC 流或测试桩均可实现）。</summary>
public interface ICommandRequestWriter
{
    Task WriteAsync(DispatchRequest request, CancellationToken ct);
}

/// <summary>Agent 命令流读入端（gRPC 流或测试桩均可实现）；流结束时返回 null。</summary>
public interface ICommandResultReader
{
    Task<DispatchResult?> ReadNextAsync(CancellationToken ct);
}

/// <summary>
/// 节点命令通道：把 Dispatch 请求经 CommandStream 转发到目标节点，
/// 按 request_id 匹配回传结果。契约正源：src/docs/protocol-contract.md §2/§4/§5。
/// </summary>
public class NodeCommandHub
{
    private sealed class NodeChannel
    {
        public Channel<DispatchRequest> Outbound { get; } =
            Channel.CreateUnbounded<DispatchRequest>(new UnboundedChannelOptions { SingleReader = true });
        public ConcurrentDictionary<string, TaskCompletionSource<DispatchResult>> Pending { get; } = new();
    }

    private readonly ConcurrentDictionary<string, NodeChannel> _channels = new();

    public bool IsConnected(string nodeId) => _channels.ContainsKey(nodeId);

    /// <summary>
    /// 挂接节点命令流：读循环按 request_id 完成在途请求；写循环推送出站请求。
    /// 流断开时摘除通道并使在途请求失败（UNAVAILABLE）。
    /// </summary>
    public async Task AttachAsync(
        string nodeId, ICommandRequestWriter writer, ICommandResultReader reader, CancellationToken ct)
    {
        var channel = _channels.TryAdd(nodeId, new NodeChannel())
            ? _channels[nodeId]
            : throw new RpcException(new Status(StatusCode.AlreadyExists, $"node {nodeId} command stream already attached"));

        try
        {
            var readLoop = Task.Run(async () =>
            {
                try
                {
                    while (await reader.ReadNextAsync(ct) is { } result)
                    {
                        if (channel.Pending.TryRemove(result.RequestId, out var tcs))
                            tcs.TrySetResult(result);
                    }
                }
                catch (OperationCanceledException) { }
                catch (RpcException) { }
                finally
                {
                    // 读侧结束（Agent 断开或流收尾）即关闭写侧，让挂接协程自然收尾，
                    // 不必等外部取消（gRPC 请求流关闭即等价于通道断开）。
                    channel.Outbound.Writer.TryComplete();
                }
            }, CancellationToken.None);

            await foreach (var request in channel.Outbound.Reader.ReadAllAsync(ct))
                await writer.WriteAsync(request, ct);

            await readLoop;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _channels.TryRemove(new KeyValuePair<string, NodeChannel>(nodeId, channel));
            FailPending(channel, "agent command stream disconnected");
        }
    }

    /// <summary>向节点转发请求并等待结果；超时抛 DEADLINE_EXCEEDED，未连接抛 UNAVAILABLE。</summary>
    public async Task<DispatchResult> DispatchAsync(
        string nodeId, DispatchRequest request, TimeSpan timeout, CancellationToken ct)
    {
        if (!_channels.TryGetValue(nodeId, out var channel))
            throw new RpcException(new Status(StatusCode.Unavailable, $"node {nodeId} command channel not connected"));

        var tcs = new TaskCompletionSource<DispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Pending[request.RequestId] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await channel.Outbound.Writer.WriteAsync(request, timeoutCts.Token);
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            channel.Pending.TryRemove(request.RequestId, out _);
            throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                $"dispatch timed out after {timeout.TotalMilliseconds}ms"));
        }
        catch (ChannelClosedException)
        {
            // 通道在读侧收尾后关闭：等价于 Agent 断开，按 UNAVAILABLE 上抛而非悬挂到超时。
            channel.Pending.TryRemove(request.RequestId, out _);
            throw new RpcException(new Status(StatusCode.Unavailable,
                $"node {nodeId} command channel disconnected"));
        }
    }

    private static void FailPending(NodeChannel channel, string reason)
    {
        foreach (var key in channel.Pending.Keys)
            if (channel.Pending.TryRemove(key, out var tcs))
                tcs.TrySetException(new RpcException(new Status(StatusCode.Unavailable, reason)));
    }
}

/// <summary>gRPC 命令流适配器：把 gRPC 双向流包装为 hub 使用的接口。</summary>
public static class CommandStreamAdapter
{
    public static ICommandRequestWriter Writer(IServerStreamWriter<DispatchRequest> stream) =>
        new GrpcRequestWriter(stream);

    public static ICommandResultReader Reader(IAsyncStreamReader<DispatchResult> stream) =>
        new GrpcResultReader(stream);

    private sealed class GrpcRequestWriter(IAsyncStreamWriter<DispatchRequest> stream) : ICommandRequestWriter
    {
        public Task WriteAsync(DispatchRequest request, CancellationToken ct) => stream.WriteAsync(request, ct);
    }

    private sealed class GrpcResultReader(IAsyncStreamReader<DispatchResult> stream) : ICommandResultReader
    {
        public async Task<DispatchResult?> ReadNextAsync(CancellationToken ct) =>
            await stream.MoveNext(ct) ? stream.Current : null;
    }
}
