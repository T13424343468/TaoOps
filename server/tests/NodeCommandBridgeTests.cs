using Grpc.Core;
using TaoOps.Server.Audit;
using TaoOps.Server.Grpc;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/protocol-contract.md §6（HTTP 下发桥，项5）。</summary>
public class NodeCommandBridgeTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "taoops-bridge-" + Guid.NewGuid().ToString("N"));
    private readonly NodeRegistry _registry = new();
    private readonly NodeCommandHub _hub = new();
    private readonly AuditStore _audit;
    private readonly NodeCommandBridge _bridge;

    public NodeCommandBridgeTests()
    {
        _audit = new AuditStore(_dataDir);
        _bridge = new NodeCommandBridge(_registry, _hub, _audit);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    // ---- 测试桩：命令流（与 AgentServiceDispatchTests 同口径）----

    private sealed class FakeRequestWriter(Action<DispatchRequest>? onWrite = null) : ICommandRequestWriter
    {
        public Task WriteAsync(DispatchRequest request, CancellationToken ct)
        {
            onWrite?.Invoke(request);
            return Task.CompletedTask;
        }
    }

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

    private string RegisterNode(string agentId = "a-1")
    {
        var node = _registry.Register(new RegisterRequest { AgentId = agentId, Hostname = "host-1" });
        return node.NodeId;
    }

    private async Task AttachChannel(string nodeId)
    {
        var reader = new FakeResultReader();
        // 写桩：请求写出即按 request_id 回填结果（模拟 Agent 即时回执）。
        var attach = _hub.AttachAsync(nodeId, new FakeRequestWriter(req =>
            reader.Enqueue(new DispatchResult { RequestId = req.RequestId, ExitCode = 0, Output = "ok" })),
            reader, CancellationToken.None);
        while (!_hub.IsConnected(nodeId)) await Task.Delay(10);
        await Task.Yield();
    }

    [Fact]
    public async Task Dispatch_UnknownNode_Throws404()
    {
        var ex = await Assert.ThrowsAsync<BridgeException>(
            () => _bridge.DispatchAsync("missing", "echo", null, 0, "op", "Operator", "127.0.0.1"));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_NonWhitelistedCommand_Throws400()
    {
        var nodeId = RegisterNode();
        var ex = await Assert.ThrowsAsync<BridgeException>(
            () => _bridge.DispatchAsync(nodeId, "rm -rf", null, 0, "op", "Operator", "127.0.0.1"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ShellArgsWithForbiddenChars_Throws400()
    {
        var nodeId = RegisterNode();
        var ex = await Assert.ThrowsAsync<BridgeException>(
            () => _bridge.DispatchAsync(nodeId, "echo", ["hello; id"], 0, "op", "Operator", "127.0.0.1"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_TimeoutOutOfRange_Throws400()
    {
        var nodeId = RegisterNode();
        var ex = await Assert.ThrowsAsync<BridgeException>(
            () => _bridge.DispatchAsync(nodeId, "uname", null, 999_999, "op", "Operator", "127.0.0.1"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ChannelNotConnected_Throws503_AndAudited()
    {
        var nodeId = RegisterNode();
        var ex = await Assert.ThrowsAsync<BridgeException>(
            () => _bridge.DispatchAsync(nodeId, "uname", null, 0, "op", "Operator", "127.0.0.1"));
        Assert.Equal(503, ex.StatusCode);
        var ev = _audit.Query("op", null, null).Single(e => e.Action == "node.cmd");
        Assert.Equal("unavailable", ev.Result);
        Assert.Equal($"/api/nodes/{nodeId}/cmd", ev.Target);
    }

    [Fact]
    public async Task Dispatch_Success_ReturnsOutcome_AndAuditedWithExitCode()
    {
        var nodeId = RegisterNode();
        await AttachChannel(nodeId);

        var outcome = await _bridge.DispatchAsync(nodeId, "uname", ["-a"], 0, "op", "Operator", "127.0.0.1");

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("ok", outcome.Output);
        Assert.NotEmpty(outcome.RequestId);

        var ev = _audit.Query("op", null, null).Single(e => e.Action == "node.cmd");
        Assert.Equal("0", ev.Result);
        Assert.Equal("Operator", ev.Role);
        Assert.Equal("127.0.0.1", ev.Ip);
        // 审计哈希链仍可验证（FR-D4）。
        Assert.True(_audit.VerifyChain().Valid);
    }

    [Fact]
    public void ListNodes_ReportsConnectionState()
    {
        var nodeId = RegisterNode();
        var view = _bridge.ListNodes().Single(n => n.NodeId == nodeId);
        Assert.Equal("host-1", view.Hostname);
        Assert.False(view.CommandConnected);

        // 挂接后 connected=true（异步挂接，轮询等待）。
        var reader = new FakeResultReader();
        _ = _hub.AttachAsync(nodeId, new FakeRequestWriter(), reader, CancellationToken.None);
        Thread.Sleep(50);
        Assert.True(_bridge.ListNodes().Single(n => n.NodeId == nodeId).CommandConnected);
    }
}
