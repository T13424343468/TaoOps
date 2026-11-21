using System.IO;
using System.Text;
using TaoOps.Server.Grpc;
using TaoOps.Server.Services;
using TaoOps.Server.Waf;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单 A7/A8：日志回流解析与事件查询。</summary>
public class WafEventIngestorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taoops-wafev-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ParseChunk_ValidTaoopsLogLine_BecomesEvent()
    {
        // Lua 侧输出形如：2026/11/22 .. [error] .. TAOOPS_WAF {"event":"deny",...}
        var ingestor = new WafEventIngestor(new NodeStreamHub(), new WafEventStore(_dir));
        var tail = new StringBuilder();
        var line = "2026/11/22 07:30:00 [error] 123#0: TAOOPS_WAF {\"event\":\"deny\",\"rule_id\":\"s1\",\"rule_name\":\"block-sqli\",\"ip\":\"1.2.3.4\",\"uri\":\"/?id=1\"}";
        var events = ingestor.ParseChunk(line + "\n", tail, "node-1").ToList();

        var ev = Assert.Single(events);
        Assert.Equal("deny", ev.Event);
        Assert.Equal("s1", ev.RuleId);
        Assert.Equal("1.2.3.4", ev.Ip);
        Assert.Equal("node-1", ev.NodeId);
        Assert.Equal("/?id=1", ev.Uri);
        Assert.Empty(tail.ToString()); // 整行消费完无半行残留
    }

    [Fact]
    public void ParseChunk_PlainJsonLine_AlsoAccepted()
    {
        var ingestor = new WafEventIngestor(new NodeStreamHub(), new WafEventStore(_dir));
        var tail = new StringBuilder();
        var events = ingestor.ParseChunk(
            "{\"event\":\"log\",\"rule_id\":\"sc1\",\"ts\":1700000000000}\n", tail, "n").ToList();
        var ev = Assert.Single(events);
        Assert.Equal("log", ev.Event);
        Assert.Equal(1700000000000, ev.Ts);
    }

    [Fact]
    public void ParseChunk_MalformedLines_SkippedNotCrash()
    {
        // A7：畸形行跳过不崩；空输入零事件。
        var ingestor = new WafEventIngestor(new NodeStreamHub(), new WafEventStore(_dir));
        var tail = new StringBuilder();
        var events = ingestor.ParseChunk(
            "not json at all\n{\"event\":\"deny\",\"rule_id\":\"ok1\"}\n\n{broken\n", tail, "n").ToList();
        Assert.Equal("ok1", Assert.Single(events).RuleId);

        Assert.Empty(ingestor.ParseChunk("", tail, "n"));
    }

    [Fact]
    public void ParseChunk_HalfLineAcrossChunks_Buffered()
    {
        var ingestor = new WafEventIngestor(new NodeStreamHub(), new WafEventStore(_dir));
        var tail = new StringBuilder();
        Assert.Empty(ingestor.ParseChunk("{\"event\":\"deny\",", tail, "n")); // 半行不产出
        var events = ingestor.ParseChunk("\"rule_id\":\"half\"}\n", tail, "n").ToList();
        Assert.Equal("half", Assert.Single(events).RuleId);
    }

    [Fact]
    public async Task StartAsync_SubscribesAndIngests()
    {
        // 端到端（单机）：LogSession 广播 chunk → Ingestor 落库。
        var hub = new NodeStreamHub();
        var store = new WafEventStore(_dir);
        var ingestor = new WafEventIngestor(hub, store);
        hub.AttachLog("node-9");
        var session = hub.GetLog("node-9")!;
        using var cts = new CancellationTokenSource();

        var run = ingestor.StartAsync("node-9", cts.Token);
        // 让订阅先挂上再广播。
        await Task.Delay(100);
        session.Broadcast(new LogChunk
        {
            NodeId = "node-9",
            Data = Google.Protobuf.ByteString.CopyFromUtf8(
                "TAOOPS_WAF {\"event\":\"deny\",\"rule_id\":\"b1\",\"ip\":\"8.8.8.8\"}\n"),
            Ts = 1700000000000,
        });
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var ev = Assert.Single(store.Query("node-9"));
        Assert.Equal("b1", ev.RuleId);
        Assert.Equal("8.8.8.8", ev.Ip);
    }

    [Fact]
    public void Query_FiltersByNodeAndTimeRange()
    {
        // A8：按节点与时间范围过滤。
        var store = new WafEventStore(_dir);
        store.Append(new WafEvent { NodeId = "n1", Event = "deny", RuleId = "a", Ts = 1000 });
        store.Append(new WafEvent { NodeId = "n2", Event = "deny", RuleId = "b", Ts = 2000 });
        store.Append(new WafEvent { NodeId = "n1", Event = "log", RuleId = "c", Ts = 3000 });

        Assert.Equal(2, store.Query("n1").Count);
        // 无节点过滤：时间倒序全部三条。
        Assert.Equal(["c", "b", "a"], store.Query(fromMs: 500, toMs: 3500).Select(e => e.RuleId).ToArray());
        Assert.Equal("c", store.Query(nodeId: "n1", fromMs: 2500).Single().RuleId);
        Assert.Empty(store.Query(toMs: 999));
    }

    [Fact]
    public void Events_PersistAcrossStoreReload()
    {
        var dir = Path.Combine(_dir, "reload");
        var store = new WafEventStore(dir);
        store.Append(new WafEvent { NodeId = "n1", Event = "deny", RuleId = "r", Ts = 42 });
        var reloaded = new WafEventStore(dir).Query();
        Assert.Equal("r", Assert.Single(reloaded).RuleId);
    }

    [Fact]
    public async Task StartAsync_UnknownNode_Throws()
    {
        var ingestor = new WafEventIngestor(new NodeStreamHub(), new WafEventStore(_dir));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ingestor.StartAsync("ghost", CancellationToken.None));
    }
}
