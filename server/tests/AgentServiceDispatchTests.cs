using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Grpc;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>指令白名单与参数校验（契约 §4）；执行路径经 NodeCommandHub 转发。</summary>
public class AgentServiceDispatchTests
{
    private sealed class FakeRequestWriter(Action<DispatchRequest>? onWrite = null) : ICommandRequestWriter
    {
        public List<DispatchRequest> Written { get; } = [];
        public Task WriteAsync(DispatchRequest request, CancellationToken ct)
        {
            Written.Add(request);
            onWrite?.Invoke(request);
            return Task.CompletedTask;
        }
    }

    /// <summary>阻塞式读桩：结果入队前挂起等待，通道完成后返回 null 表示流结束。</summary>
    private sealed class FakeResultReader : ICommandResultReader
    {
        private readonly System.Threading.Channels.Channel<DispatchResult> _results =
            System.Threading.Channels.Channel.CreateUnbounded<DispatchResult>();

        public void Enqueue(DispatchResult result) => _results.Writer.TryWrite(result);

        public void Complete() => _results.Writer.TryComplete();

        public async Task<DispatchResult?> ReadNextAsync(CancellationToken ct)
        {
            while (await _results.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                if (_results.Reader.TryRead(out var r))
                    return r;
            return null;
        }
    }

    private static (Services.AgentService Service, NodeRegistry Registry, NodeCommandHub Hub) Create()
    {
        var registry = new NodeRegistry();
        var hub = new NodeCommandHub();
        var service = new Services.AgentService(registry, hub, new NodeStreamHub(), NullLogger<Services.AgentService>.Instance);
        return (service, registry, hub);
    }

    private static (Services.AgentService Service, NodeRegistry Registry, NodeCommandHub Hub, NodeStreamHub Streams) CreateFull()
    {
        var registry = new NodeRegistry();
        var hub = new NodeCommandHub();
        var streams = new NodeStreamHub();
        var service = new Services.AgentService(registry, hub, streams, NullLogger<Services.AgentService>.Instance);
        return (service, registry, hub, streams);
    }

    private static string RegisterNode(NodeRegistry registry)
    {
        var node = registry.Register(new RegisterRequest { AgentId = "a-1", Hostname = "h" });
        return node.NodeId;
    }

    [Fact]
    public async Task Dispatch_UnknownNode_ThrowsNotFound()
    {
        var (service, _, _) = Create();
        var req = new DispatchRequest { NodeId = "missing", Command = "echo" };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_CommandNotInWhitelist_ThrowsInvalidArgument()
    {
        var (service, registry, _) = Create();
        var nodeId = RegisterNode(registry);
        var req = new DispatchRequest { NodeId = nodeId, Command = "rm" };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ModuleCommandNotInWhitelist_ThrowsInvalidArgument()
    {
        var (service, registry, _) = Create();
        var nodeId = RegisterNode(registry);
        var req = new DispatchRequest { NodeId = nodeId, Command = "kernel.tamper" };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ShellArgWithInjectionChar_ThrowsInvalidArgument()
    {
        var (service, registry, _) = Create();
        var nodeId = RegisterNode(registry);
        var req = new DispatchRequest { NodeId = nodeId, Command = "echo", Args = { "hello; rm -rf /" } };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_TimeoutOutOfRange_ThrowsInvalidArgument()
    {
        var (service, registry, _) = Create();
        var nodeId = RegisterNode(registry);
        var req = new DispatchRequest { NodeId = nodeId, Command = "echo", TimeoutMs = 999_999 };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_NodeNotConnected_ThrowsUnavailable()
    {
        var (service, registry, _) = Create();
        var nodeId = RegisterNode(registry);
        var req = new DispatchRequest { NodeId = nodeId, Command = "echo" };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_EchoHello_EndToEndViaHub()
    {
        var (service, registry, hub) = Create();
        var nodeId = RegisterNode(registry);
        using var cts = new CancellationTokenSource();

        // 挂接一个"收到请求即按 request_id 回结果"的假 Agent
        var reader = new FakeResultReader();
        var writer = new FakeRequestWriter(req =>
            reader.Enqueue(new DispatchResult { ExitCode = 0, Output = "hello", RequestId = req.RequestId }));
        var session = hub.AttachAsync(nodeId, writer, reader, cts.Token);

        var result = await service.Dispatch(
            new DispatchRequest { NodeId = nodeId, Command = "echo", Args = { "hello" } },
            TestCallContext());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.Output);
        cts.Cancel();
    }

    [Fact]
    public async Task Dispatch_AgentSilent_ThrowsDeadlineExceeded()
    {
        var (service, registry, hub) = Create();
        var nodeId = RegisterNode(registry);
        using var cts = new CancellationTokenSource();

        var writer = new FakeRequestWriter();
        var session = hub.AttachAsync(nodeId, writer, new FakeResultReader(), cts.Token);

        var req = new DispatchRequest { NodeId = nodeId, Command = "echo", TimeoutMs = 50 };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.DeadlineExceeded, ex.StatusCode);
        cts.Cancel();
    }

    [Fact]
    public async Task CommandStream_UnknownNode_ThrowsNotFound()
    {
        var (service, _, _) = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.CommandStream(NullResultReader(), NullRequestWriter(), CallContextWithNodeHeader("bogus-node")));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task CommandStream_MissingHeader_ThrowsNotFound()
    {
        var (service, _, _) = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.CommandStream(NullResultReader(), NullRequestWriter(), CallContextWithNodeHeader(null)));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Register_EmptyAgentId_ThrowsInvalidArgument()
    {
        var (service, _, _) = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => service.Register(new RegisterRequest { AgentId = "" }, TestCallContext()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // ---- 流式通道全覆盖（Register 成功路径 / Heartbeat / CommandStream / TermStream / LogStream）----

    [Fact]
    public async Task Register_ValidAgent_ReturnsNodeIdAndConfig()
    {
        var (service, registry, _) = Create();
        var res = await service.Register(new RegisterRequest { AgentId = "a-9", Hostname = "host-9" }, TestCallContext());
        Assert.False(string.IsNullOrEmpty(res.NodeId));
        Assert.Equal(15_000, res.Config.HeartbeatIntervalMs);
        Assert.Equal(1, registry.Count);
    }

    private sealed class FakeStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _enumerator;
        public FakeStreamReader(params T[] items) => _enumerator = ((IEnumerable<T>)items).GetEnumerator();
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(_enumerator.MoveNext());
        public T Current => _enumerator.Current!;
    }

    /// <summary>保持打开的读桩：初始项取完后挂起等待，CompleteStream() 才视为流收尾。
    /// 模拟真实 gRPC 请求流「连着但暂无数据」的形态，用于断言下行泵在流收尾前持续工作。</summary>
    private sealed class HeldStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly System.Threading.Channels.Channel<T> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<T>();

        public HeldStreamReader(params T[] initial)
        {
            foreach (var item in initial)
                _channel.Writer.TryWrite(item);
        }

        public void CompleteStream() => _channel.Writer.TryComplete();

        public async Task<bool> MoveNext(CancellationToken cancellationToken) =>
            await _channel.Reader.WaitToReadAsync(cancellationToken);
        public T Current => _channel.Reader.TryRead(out var item) ? item : throw new InvalidOperationException("no current item");
    }

    private sealed class FakeStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message) => WriteAsync(message, CancellationToken.None);
        public Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Heartbeat_KnownNode_AcksEachMessage()
    {
        var (service, registry, _) = Create();
        var nodeId = RegisterNode(registry);
        var acks = new FakeStreamWriter<HeartbeatAck>();
        await service.Heartbeat(
            new FakeStreamReader<HeartbeatMsg>(
                new HeartbeatMsg { NodeId = nodeId },
                new HeartbeatMsg { NodeId = nodeId }),
            acks, TestCallContext());
        Assert.Equal(2, acks.Written.Count);
        Assert.True(acks.Written[0].Ts > 0);
    }

    [Fact]
    public async Task Heartbeat_UnknownNode_ThrowsNotFound()
    {
        var (service, _, _) = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Heartbeat(
            new FakeStreamReader<HeartbeatMsg>(new HeartbeatMsg { NodeId = "ghost" }),
            new FakeStreamWriter<HeartbeatAck>(), TestCallContext()));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task CommandStream_ValidNode_AttachesAndCompletesOnStreamEnd()
    {
        var (service, registry, hub) = Create();
        var nodeId = RegisterNode(registry);
        var ctx = CallContextWithNodeHeader(nodeId);

        // 请求流带一条结果后收尾：读循环完成 → 通道优雅关闭 → 挂接协程退出。
        var resultStream = new FakeStreamReader<DispatchResult>(
            new DispatchResult { RequestId = "r-1", ExitCode = 0 });
        await service.CommandStream(resultStream, new FakeGrpcRequestWriter(), ctx);

        Assert.False(hub.IsConnected(nodeId)); // 流收尾即摘除通道
    }

    [Fact]
    public async Task TermStream_PumpsBothDirections_ThenDetaches()
    {
        var (service, registry, _, streams) = CreateFull();
        var nodeId = RegisterNode(registry);
        var ctx = CallContextWithNodeHeader(nodeId, sessionId: "sess-it-1");

        var toAgent = new FakeStreamWriter<TermData>();
        var fromAgent = new HeldStreamReader<TermData>(
            new TermData { NodeId = nodeId, SessionId = "sess-it-1", Data = Google.Protobuf.ByteString.CopyFromUtf8("out") });

        var run = service.TermStream(fromAgent, toAgent, ctx);

        // 会话经 AttachTerm 挂在 service 的 streams 上，取回做双向泵断言。
        var session = streams.GetTerm(nodeId, "sess-it-1");
        Assert.NotNull(session);

        // 中控 → Agent：写 Outbound，responseStream 收到。
        await session!.Outbound.Writer.WriteAsync(new TermData { NodeId = nodeId, SessionId = "sess-it-1", Data = Google.Protobuf.ByteString.CopyFromUtf8("in") });
        await Task.Delay(100);
        Assert.Contains(toAgent.Written, t => t.Data.ToStringUtf8() == "in");

        // 请求流收尾 → 泵退出 → 会话摘除。
        fromAgent.CompleteStream();
        await run;
        Assert.Null(streams.GetTerm(nodeId, "sess-it-1"));
    }

    [Fact]
    public async Task TermStream_MissingSessionHeader_ThrowsInvalidArgument()
    {
        var (service, registry, _) = Create();
        var nodeId = RegisterNode(registry);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.TermStream(new FakeStreamReader<TermData>(), new FakeStreamWriter<TermData>(),
                CallContextWithNodeHeader(nodeId, sessionId: null)));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task TermStream_UnknownNode_ThrowsNotFound()
    {
        var (service, _, _) = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.TermStream(new FakeStreamReader<TermData>(), new FakeStreamWriter<TermData>(),
                CallContextWithNodeHeader("ghost", sessionId: "s")));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task LogStream_BroadcastsChunks_ThenDetaches()
    {
        var (service, registry, _, streams) = CreateFull();
        var nodeId = RegisterNode(registry);
        var ctx = CallContextWithNodeHeader(nodeId);

        var controls = new FakeStreamWriter<LogControl>();
        var chunks = new HeldStreamReader<LogChunk>(
            new LogChunk { NodeId = nodeId, Data = Google.Protobuf.ByteString.CopyFromUtf8("waf-line\n") });

        var run = service.LogStream(chunks, controls, ctx);
        await Task.Delay(100); // 让读循环把 chunk 广播出去

        // Agent 日志块经会话广播到订阅者（WafEventIngestor 消费的正是这条路径）。
        var session = streams.GetLog(nodeId);
        Assert.NotNull(session);
        using var sub = session!.Subscribe(out var reader);
        session.Broadcast(new LogChunk { NodeId = nodeId, Data = Google.Protobuf.ByteString.CopyFromUtf8("late\n") });
        var got = await reader.ReadAsync();
        Assert.Equal("late\n", got.Data.ToStringUtf8());

        // 中控 → Agent：订阅控制经 Outbound 泵到 responseStream。
        await session.Outbound.Writer.WriteAsync(new LogControl { Action = "subscribe", Path = "/var/log/x" });
        await Task.Delay(100);
        Assert.Contains(controls.Written, c => c.Action == "subscribe");

        chunks.CompleteStream();
        await run; // 请求流收尾 → 泵退出 → 会话摘除
        Assert.Null(streams.GetLog(nodeId));
    }

    [Fact]
    public async Task LogStream_UnknownNode_ThrowsNotFound()
    {
        var (service, _, _) = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.LogStream(new FakeStreamReader<LogChunk>(), new FakeStreamWriter<LogControl>(),
                CallContextWithNodeHeader("ghost")));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    private static IAsyncStreamReader<DispatchResult> NullResultReader() =>
        new FakeGrpcResultReader();

    private static IServerStreamWriter<DispatchRequest> NullRequestWriter() =>
        new FakeGrpcRequestWriter();

    private sealed class FakeGrpcResultReader : IAsyncStreamReader<DispatchResult>
    {
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
        public DispatchResult Current => null!;
    }

    private sealed class FakeGrpcRequestWriter : IServerStreamWriter<DispatchRequest>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(DispatchRequest message) => Task.CompletedTask;
        public Task WriteAsync(DispatchRequest message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ServerCallContext CallContextWithNodeHeader(string? nodeId, string? sessionId = null)
    {
        var ctx = new TestServerCallContext();
        if (nodeId is not null)
            ctx.RequestHeadersInternal.Add(new Metadata.Entry("x-node-id", nodeId));
        if (sessionId is not null)
            ctx.RequestHeadersInternal.Add(new Metadata.Entry("x-session-id", sessionId));
        return ctx;
    }

    private static ServerCallContext TestCallContext() => new TestServerCallContext();

    private sealed class TestServerCallContext : ServerCallContext
    {
        public Metadata RequestHeadersInternal { get; } = new();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        protected override Metadata RequestHeadersCore => RequestHeadersInternal;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; } = Status.DefaultSuccess;
        protected override string MethodCore => "/taoops.v1.AgentService/Dispatch";
        protected override string PeerCore => "ipv4:127.0.0.1:1";
        protected override AuthContext AuthContextCore => new AuthContext("", new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotImplementedException();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override string HostCore => "";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
    }
}
