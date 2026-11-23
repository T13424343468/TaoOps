using Grpc.Core;
using TaoOps.Server.Grpc;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>命令通道生命周期：挂接/重复挂接/优雅断开/断开时在途请求失败。</summary>
public class NodeCommandHubTests
{
    private sealed class BlockingReader : ICommandResultReader
    {
        private readonly TaskCompletionSource<DispatchResult?> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate.TrySetResult(null);
        public void Fail(Exception ex) => _gate.TrySetException(ex);
        public async Task<DispatchResult?> ReadNextAsync(CancellationToken ct)
        {
            var done = await Task.WhenAny(_gate.Task, Task.Delay(Timeout.Infinite, ct));
            if (done != _gate.Task) await Task.FromCanceled<DispatchResult?>(ct);
            return await _gate.Task;
        }
    }

    private sealed class SilentWriter : ICommandRequestWriter
    {
        public Task WriteAsync(DispatchRequest request, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>写端卡在首次 WriteAsync 直到放行：制造"通道已关但挂接协程未退出"的确定性窗口。</summary>
    private sealed class GatedWriter : ICommandRequestWriter
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void WaitEntered() => _entered.Task.Wait(TimeSpan.FromSeconds(5));
        public void Release() => _gate.TrySetResult();
        public async Task WriteAsync(DispatchRequest request, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _gate.Task.WaitAsync(ct);
        }
    }

    private static async Task<(NodeCommandHub Hub, string NodeId, BlockingReader Reader, Task Attached)> AttachAsync(
        NodeCommandHub hub, string nodeId)
    {
        var reader = new BlockingReader();
        var attached = hub.AttachAsync(nodeId, new SilentWriter(), reader, CancellationToken.None);
        // 等挂接生效（IsConnected 翻转）。
        for (var i = 0; i < 100 && !hub.IsConnected(nodeId); i++) await Task.Delay(10);
        return (hub, nodeId, reader, attached);
    }

    [Fact]
    public async Task IsConnected_FlipsWithAttachAndDetach()
    {
        var hub = new NodeCommandHub();
        Assert.False(hub.IsConnected("n1"));
        var (_, _, reader, attached) = await AttachAsync(hub, "n1");
        Assert.True(hub.IsConnected("n1"));

        reader.Release(); // 读侧结束 → 写侧收尾 → 挂接协程自然退出
        await attached;
        Assert.False(hub.IsConnected("n1"));
    }

    [Fact]
    public async Task Attach_Twice_ThrowsAlreadyExists()
    {
        var hub = new NodeCommandHub();
        var (_, _, reader, attached) = await AttachAsync(hub, "n1");
        await Assert.ThrowsAsync<RpcException>(() =>
            hub.AttachAsync("n1", new SilentWriter(), new BlockingReader(), CancellationToken.None));
        reader.Release();
        await attached;
    }

    [Fact]
    public async Task ReaderThrowsRpcException_AttachEndsCleanly()
    {
        var hub = new NodeCommandHub();
        var reader = new BlockingReader();
        var attached = hub.AttachAsync("n1", new SilentWriter(), reader, CancellationToken.None);
        for (var i = 0; i < 100 && !hub.IsConnected("n1"); i++) await Task.Delay(10);

        reader.Fail(new RpcException(new Status(StatusCode.Unavailable, "stream reset")));
        await attached;
        Assert.False(hub.IsConnected("n1"));
    }

    [Fact]
    public async Task Disconnect_FailsPendingDispatch_WithUnavailable()
    {
        var hub = new NodeCommandHub();
        var (_, _, reader, attached) = await AttachAsync(hub, "n1");

        var dispatch = hub.DispatchAsync("n1", new DispatchRequest { NodeId = "n1", RequestId = "r-1" },
            TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(50); // 让请求进入在途队列

        reader.Release(); // 断开：在途请求必须立刻失败而不是悬挂到超时
        var ex = await Assert.ThrowsAsync<RpcException>(() => dispatch);
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        await attached;
    }

    [Fact]
    public async Task Dispatch_AfterGracefulClose_ThrowsUnavailable()
    {
        var hub = new NodeCommandHub();
        var (_, _, reader, attached) = await AttachAsync(hub, "n1");
        reader.Release();
        await attached;

        var ex = await Assert.ThrowsAsync<RpcException>(() => hub.DispatchAsync(
            "n1", new DispatchRequest { NodeId = "n1" }, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_IntoClosedChannel_ThrowsUnavailable_NotHang()
    {
        // 确定性竞态窗口：写循环卡在首次 WriteAsync 时读侧收尾（通道关闭但未摘除），
        // 此刻新 Dispatch 命中已关通道 → 立刻 UNAVAILABLE，而非悬挂到超时。
        var hub = new NodeCommandHub();
        var writer = new GatedWriter();
        var reader = new BlockingReader();
        var attached = hub.AttachAsync("n1", writer, reader, CancellationToken.None);
        for (var i = 0; i < 100 && !hub.IsConnected("n1"); i++) await Task.Delay(10);

        var first = hub.DispatchAsync("n1", new DispatchRequest { NodeId = "n1", RequestId = "r-1" },
            TimeSpan.FromSeconds(30), CancellationToken.None);
        writer.WaitEntered(); // 写循环已消费请求并卡住

        reader.Release();      // 读侧收尾：通道写侧关闭，但挂接协程仍卡在写端
        await Task.Delay(50);  // 让 TryComplete 先落定

        var ex = await Assert.ThrowsAsync<RpcException>(() => hub.DispatchAsync(
            "n1", new DispatchRequest { NodeId = "n1", RequestId = "r-2" },
            TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);

        writer.Release(); // 放行写循环 → 挂接协程退出，首个在途请求按断开失败
        var firstEx = await Assert.ThrowsAsync<RpcException>(() => first);
        Assert.Equal(StatusCode.Unavailable, firstEx.StatusCode);
        await attached;
    }
}
