using System.Collections.Concurrent;
using System.Threading.Channels;
using Grpc.Core;
using TaoOps.Server.Grpc;

namespace TaoOps.Server.Services;

/// <summary>
/// 终端/日志流枢纽（M2）：Agent 经 TermStream/LogStream 挂接会话，
/// 中控侧 WebSocket 桥接控制台连接。契约正源：src/docs/protocol-contract.md §1/§2/§5。
/// </summary>
public class NodeStreamHub
{
    /// <summary>一个终端会话：Outbound 为中控→Agent 输入，Inbound 为 Agent→中控输出。</summary>
    public sealed class TermSession
    {
        public required string SessionId { get; init; }
        public required string NodeId { get; init; }
        public Channel<TermData> Outbound { get; } = Channel.CreateUnbounded<TermData>();
        public Channel<TermData> Inbound { get; } = Channel.CreateUnbounded<TermData>();
    }

    /// <summary>一个节点的日志流：Outbound 为中控→Agent 订阅控制，Chunk 广播给订阅者。</summary>
    public sealed class LogSession
    {
        public required string NodeId { get; init; }
        public Channel<LogControl> Outbound { get; } = Channel.CreateUnbounded<LogControl>();
        private readonly ConcurrentDictionary<int, Channel<LogChunk>> _subscribers = new();

        public IDisposable Subscribe(out ChannelReader<LogChunk> reader)
        {
            var ch = Channel.CreateUnbounded<LogChunk>();
            // 键单调递增且不复用（只增不回绕的 int），TryAdd 不会撞键，无需重试循环。
            var key = System.Threading.Interlocked.Increment(ref _subscriberSeq);
            _subscribers.TryAdd(key, ch);
            reader = ch.Reader;
            return new Unsubscriber(_subscribers, key);
        }

        private static int _subscriberSeq;

        internal void Broadcast(LogChunk chunk)
        {
            foreach (var s in _subscribers.Values)
                s.Writer.TryWrite(chunk);
        }

        internal void CloseSubscribers()
        {
            foreach (var s in _subscribers.Values)
                s.Writer.TryComplete();
            _subscribers.Clear();
        }

        private sealed class Unsubscriber(ConcurrentDictionary<int, Channel<LogChunk>> subs, int key) : IDisposable
        {
            public void Dispose()
            {
                if (subs.TryRemove(key, out var ch)) ch.Writer.TryComplete();
            }
        }
    }

    private sealed class LogSessionState
    {
        public LogSession Session { get; init; } = null!;
        public int RefCount;
    }

    private readonly ConcurrentDictionary<string, TermSession> _terms = new();
    private readonly ConcurrentDictionary<string, LogSessionState> _logs = new();

    // ---- 终端 ----

    /// <summary>Agent 挂接终端流；重复 session_id 返回 AlreadyExists。</summary>
    public TermSession AttachTerm(string nodeId, string sessionId)
    {
        var session = new TermSession { NodeId = nodeId, SessionId = sessionId };
        return _terms.TryAdd(sessionId, session)
            ? session
            : throw new RpcException(new Status(StatusCode.AlreadyExists, $"term session {sessionId} already attached"));
    }

    /// <summary>控制台取终端会话：指定 session 精确匹配，未指定取该节点最新会话。</summary>
    public TermSession? GetTerm(string nodeId, string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
            return _terms.TryGetValue(sessionId, out var s) && s.NodeId == nodeId ? s : null;
        TermSession? latest = null;
        foreach (var s in _terms.Values)
            if (s.NodeId == nodeId && (latest is null || string.CompareOrdinal(s.SessionId, latest.SessionId) > 0))
                latest = s;
        return latest;
    }

    /// <summary>Agent 流断开：摘除会话并关闭两端通道。</summary>
    public void DetachTerm(string sessionId)
    {
        if (_terms.TryRemove(sessionId, out var s))
        {
            s.Outbound.Writer.TryComplete();
            s.Inbound.Writer.TryComplete();
        }
    }

    // ---- 日志 ----

    /// <summary>Agent 挂接日志流；节点已有流返回 AlreadyExists。</summary>
    public LogSession AttachLog(string nodeId)
    {
        var state = _logs.AddOrUpdate(nodeId,
            _ => new LogSessionState { Session = new LogSession { NodeId = nodeId } },
            (_, existing) => { existing.RefCount++; return existing; });
        if (state.RefCount > 0)
        {
            state.RefCount--;
            throw new RpcException(new Status(StatusCode.AlreadyExists, $"node {nodeId} log stream already attached"));
        }
        return state.Session;
    }

    public LogSession? GetLog(string nodeId) =>
        _logs.TryGetValue(nodeId, out var state) ? state.Session : null;

    public void DetachLog(string nodeId)
    {
        if (_logs.TryRemove(nodeId, out var state))
        {
            state.Session.Outbound.Writer.TryComplete();
            state.Session.CloseSubscribers();
        }
    }
}
