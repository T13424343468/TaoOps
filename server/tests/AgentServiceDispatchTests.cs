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

    private static ServerCallContext CallContextWithNodeHeader(string? nodeId)
    {
        var ctx = new TestServerCallContext();
        if (nodeId is not null)
            ctx.RequestHeadersInternal.Add(new Metadata.Entry("x-node-id", nodeId));
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
