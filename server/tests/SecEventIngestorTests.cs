using System.Text;
using TaoOps.Server.Sec;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/breakpoint-m3-security.md A7（SEC 事件回流解析）。</summary>
public class SecEventIngestorTests
{
    [Fact]
    public void ParseChunk_HandlesSplitLines_AndSkipsMalformed()
    {
        var ingestor = new SecEventIngestor(null!, new SecEventStore(Path.Combine(Path.GetTempPath(), "taoops-sec-" + Guid.NewGuid().ToString("N"))));
        var tail = new StringBuilder();

        // 第一段只有半行。
        var half = "{\"event\":\"ssh_lock\",\"ip\":\"203";
        var r1 = ingestor.ParseChunk(half, tail, "node-1").ToList();
        Assert.Empty(r1);

        // 第二段补全该行并带一条畸形行。
        var rest = ".0.113.9\",\"failures\":5,\"ts\":1700000000000}\nnot-json-at-all\n" +
                   "TAOOPS_SEC {\"event\":\"ssh_lockout\",\"ip\":\"198.51.100.1\",\"failures\":7,\"ts\":1700000000001}\n";
        var r2 = ingestor.ParseChunk(rest, tail, "node-1").ToList();
        Assert.Equal(2, r2.Count);
        Assert.Equal("203.0.113.9", r2[0].Ip);
        Assert.Equal("node-1", r2[0].NodeId);
        Assert.Equal("198.51.100.1", r2[1].Ip);
        Assert.Equal(7, r2[1].Failures);
    }

    [Fact]
    public void Store_AppendQueryRoundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taoops-sec-" + Guid.NewGuid().ToString("N"));
        var store = new SecEventStore(dir);
        store.Append(new SecEvent { NodeId = "n1", Event = "ssh_lockout", Ip = "1.2.3.4", Failures = 5, Ts = 100 });
        store.Append(new SecEvent { NodeId = "n2", Event = "ssh_lockout", Ip = "5.6.7.8", Failures = 6, Ts = 200 });
        Assert.Single(store.Query("n1"));
        Assert.Equal(200, store.Query()[0].Ts); // 最新在前
        // 重载对称。
        var reloaded = new SecEventStore(dir);
        Assert.Equal(2, reloaded.Query().Count);
    }
}
