using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Grpc;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>指令白名单与参数校验（契约 §4）。</summary>
public class AgentServiceDispatchTests
{
    private static (Services.AgentService Service, NodeRegistry Registry) Create()
    {
        var registry = new NodeRegistry();
        var service = new Services.AgentService(registry, NullLogger<Services.AgentService>.Instance);
        return (service, registry);
    }

    [Fact]
    public async Task Dispatch_UnknownNode_ThrowsNotFound()
    {
        var (service, _) = Create();
        var req = new DispatchRequest { NodeId = "missing", Command = "echo" };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_CommandNotInWhitelist_ThrowsInvalidArgument()
    {
        var (service, registry) = Create();
        var node = registry.Register(new RegisterRequest { AgentId = "a-1", Hostname = "h" });
        var req = new DispatchRequest { NodeId = node.NodeId, Command = "rm" };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_EchoHello_ReturnsOutputAndZeroExit()
    {
        var (service, registry) = Create();
        var node = registry.Register(new RegisterRequest { AgentId = "a-1", Hostname = "h" });
        var req = new DispatchRequest { NodeId = node.NodeId, Command = "echo", Args = { "hello" } };
        var result = await service.Dispatch(req, TestCallContext());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.Output.Trim());
    }

    [Fact]
    public async Task Dispatch_TimeoutOutOfRange_ThrowsInvalidArgument()
    {
        var (service, registry) = Create();
        var node = registry.Register(new RegisterRequest { AgentId = "a-1", Hostname = "h" });
        var req = new DispatchRequest { NodeId = node.NodeId, Command = "echo", TimeoutMs = 999_999 };
        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Dispatch(req, TestCallContext()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Register_EmptyAgentId_ThrowsInvalidArgument()
    {
        var (service, _) = Create();
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => service.Register(new RegisterRequest { AgentId = "" }, TestCallContext()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    private static ServerCallContext TestCallContext() => new TestServerCallContext();

    private sealed class TestServerCallContext : ServerCallContext
    {
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        protected override Metadata RequestHeadersCore => new();
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
