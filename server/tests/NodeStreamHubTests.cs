using Grpc.Core;
using Google.Protobuf;
using TaoOps.Server.Grpc;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>NodeStreamHub 终端/日志会话管理（M2，契约 §2/§5）。</summary>
public class NodeStreamHubTests
{
    [Fact]
    public void AttachTerm_GetTerm_RoundTrip()
    {
        var hub = new NodeStreamHub();
        var s = hub.AttachTerm("n1", "sess-1");
        Assert.Equal("n1", s.NodeId);
        Assert.Same(s, hub.GetTerm("n1", "sess-1"));
        Assert.Null(hub.GetTerm("n2", "sess-1")); // 节点不匹配
    }

    [Fact]
    public void AttachTerm_DuplicateSession_ThrowsAlreadyExists()
    {
        var hub = new NodeStreamHub();
        hub.AttachTerm("n1", "dup");
        var ex = Assert.Throws<RpcException>(() => { hub.AttachTerm("n1", "dup"); });
        Assert.Equal(StatusCode.AlreadyExists, ex.StatusCode);
    }

    [Fact]
    public void GetTerm_WithoutSession_ReturnsLatest()
    {
        var hub = new NodeStreamHub();
        hub.AttachTerm("n1", "sess-1");
        var latest = hub.AttachTerm("n1", "sess-2");
        Assert.Same(latest, hub.GetTerm("n1", null));
    }

    [Fact]
    public void DetachTerm_ClosesChannels_AndRemovesSession()
    {
        var hub = new NodeStreamHub();
        var s = hub.AttachTerm("n1", "sess-1");
        hub.DetachTerm("sess-1");
        Assert.Null(hub.GetTerm("n1", "sess-1"));
        Assert.True(s.Outbound.Reader.Completion.IsCompleted);
        Assert.True(s.Inbound.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void AttachLog_SecondAttach_ThrowsAlreadyExists()
    {
        var hub = new NodeStreamHub();
        hub.AttachLog("n1");
        var ex = Assert.Throws<RpcException>(() => { hub.AttachLog("n1"); });
        Assert.Equal(StatusCode.AlreadyExists, ex.StatusCode);
    }

    [Fact]
    public void LogBroadcast_ReachesSubscriber_UntilDetach()
    {
        var hub = new NodeStreamHub();
        var session = hub.AttachLog("n1");
        var sub = session.Subscribe(out var reader);
        session.Broadcast(new LogChunk { Data = ByteString.CopyFromUtf8("line-1") });
        Assert.True(reader.TryRead(out var chunk));
        Assert.Equal("line-1", chunk.Data.ToStringUtf8());

        hub.DetachLog("n1");
        Assert.True(reader.Completion.IsCompleted); // 摘除后订阅通道关闭
        sub.Dispose();
    }

    [Fact]
    public void SubscribeDispose_StopsDelivery()
    {
        var hub = new NodeStreamHub();
        var session = hub.AttachLog("n1");
        var sub = session.Subscribe(out var reader);
        sub.Dispose();
        session.Broadcast(new LogChunk { Data = ByteString.CopyFromUtf8("after-dispose") });
        Assert.False(reader.TryRead(out _));
    }
}
