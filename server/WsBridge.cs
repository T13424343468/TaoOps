using System.Net.WebSockets;
using Grpc.Core;
using TaoOps.Server.Grpc;

namespace TaoOps.Server.Services;

/// <summary>
/// WebSocket 桥接（M2）：把控制台的终端/日志 WS 连接接到 Agent 的
/// TermStream/LogStream 会话。契约 §1/§5。
/// </summary>
public static class WsBridge
{
    /// <summary>终端：WS 双向透传字节到终端会话。</summary>
    public static async Task MapTerminal(HttpContext ctx, NodeStreamHub hub, string nodeId, string? sessionId)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var session = hub.GetTerm(nodeId, sessionId);
        if (session is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        // 生命周期自管：不挂 RequestAborted（TestServer 在 101 握手完成即标记请求结束，
        // 挂接会瞬间取消整条流）。断开检测靠 WS 帧与收发异常，语义不变。
        using var linked = new CancellationTokenSource();

        var pumpOut = Task.Run(async () =>
        {
            await foreach (var data in session.Inbound.Reader.ReadAllAsync(linked.Token))
                await WsSendAsync(ws, data.Data.ToArray(), linked.Token);
        }, linked.Token);

        var buffer = new byte[64 * 1024];
        try
        {
            while (!linked.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, linked.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.Count == 0) continue;
                var chunk = new byte[result.Count];
                Array.Copy(buffer, chunk, result.Count);
                await session.Outbound.Writer.WriteAsync(
                    new TermData { NodeId = nodeId, SessionId = session.SessionId, Data = Google.Protobuf.ByteString.CopyFrom(chunk) },
                    linked.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        finally
        {
            linked.Cancel();
            try { await pumpOut; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>日志：连接即向 Agent 下发订阅控制，LogChunk 广播回 WS。
    /// target=container 时 path 携带容器名（FR-A11 容器日志回流）。</summary>
    public static async Task MapLogs(HttpContext ctx, NodeStreamHub hub, string nodeId, string path, int tail, string target = "file")
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var session = hub.GetLog(nodeId);
        if (session is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        // 生命周期自管：不挂 RequestAborted（同 MapTerminal 口径，TestServer 101 即结束请求）。
        using var linked = new CancellationTokenSource();

        await session.Outbound.Writer.WriteAsync(new LogControl
        {
            Action = "subscribe",
            Path = path,
            TailLines = tail,
            Target = target,
        }, linked.Token);

        using var sub = session.Subscribe(out var reader);
        // 广播泵：LogChunk → WS。与 MapTerminal 同构，先起泵再进接收循环，
        // 保证升级握手后服务端即触碰 WS（TestServer 依赖首次收发完成 101 握手）。
        var pump = Task.Run(async () =>
        {
            await foreach (var chunk in reader.ReadAllAsync(linked.Token))
                await WsSendAsync(ws, chunk.Data.ToArray(), linked.Token);
        }, linked.Token);
        try
        {
            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        finally
        {
            linked.Cancel();
            try { await pump; } catch (OperationCanceledException) { }
            // 控制台断开即退订，避免 Agent 侧 tail 协程按 path 累积泄漏。
            session.Outbound.Writer.TryWrite(new LogControl { Action = "unsubscribe", Path = path });
        }
    }

    private static async Task WsSendAsync(WebSocket ws, byte[] data, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
    }
}
