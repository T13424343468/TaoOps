using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

public class NodeRegistryTests
{
    [Fact]
    public void Register_SameAgentId_ReturnsSameNodeId()
    {
        var registry = new NodeRegistry();
        var req = new TaoOps.Server.Grpc.RegisterRequest
        {
            AgentId = "a-1", Hostname = "h1", Os = "linux", Arch = "amd64", Version = "0.1.0",
        };
        var first = registry.Register(req);
        var second = registry.Register(req);
        Assert.Equal(first.NodeId, second.NodeId);
    }

    [Fact]
    public void Register_DifferentAgentId_ReturnsDifferentNodeId()
    {
        var registry = new NodeRegistry();
        var req = new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a-1", Hostname = "h1" };
        var req2 = new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a-2", Hostname = "h2" };
        Assert.NotEqual(registry.Register(req).NodeId, registry.Register(req2).NodeId);
    }

    [Fact]
    public void Touch_UnknownNode_ReturnsFalse()
    {
        var registry = new NodeRegistry();
        Assert.False(registry.Touch("no-such-node"));
    }

    [Fact]
    public void Touch_KnownNode_UpdatesLastSeen()
    {
        var registry = new NodeRegistry();
        var node = registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a-1", Hostname = "h1" });
        var before = node.LastSeen;
        Thread.Sleep(5);
        Assert.True(registry.Touch(node.NodeId));
        Assert.True(node.LastSeen > before);
    }
}
