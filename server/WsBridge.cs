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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);

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
        finally
        {
            linked.Cancel();
            try { await pumpOut; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>日志：连接即向 Agent 下发订阅控制，LogChunk 广播回 WS。</summary>
    public static async Task MapLogs(HttpContext ctx, NodeStreamHub hub, string nodeId, string path, int tail)
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);

        await session.Outbound.Writer.WriteAsync(new LogControl
        {
            Action = "subscribe",
            Path = path,
            TailLines = tail,
        }, linked.Token);

        using var sub = session.Subscribe(out var reader);
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(linked.Token))
                await WsSendAsync(ws, chunk.Data.ToArray(), linked.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            linked.Cancel();
        }
    }

    private static async Task WsSendAsync(WebSocket ws, byte[] data, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
    }
}
