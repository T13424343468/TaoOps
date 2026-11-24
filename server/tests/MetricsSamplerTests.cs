using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A23 资源实时监控：内存环形缓冲容量 1440 超限淘汰最旧、按时间窗查询、
/// 未知节点空集；采样器对在线节点取快照入库、离线跳过、失败静默留空档。
/// </summary>
public class MetricsStoreTests
{
    private static MetricsStore.MetricPoint P(long ts, double cpu = 10) =>
        new(ts, cpu, 1000, 500, 1, 1, 1, 1);

    [Fact]
    public void Add_EvictsOldestBeyondCapacity()
    {
        var store = new MetricsStore();
        for (long i = 0; i < MetricsStore.Capacity + 10; i++)
            store.Add("n1", P(i));
        var all = store.Query("n1");
        Assert.Equal(MetricsStore.Capacity, all.Count);
        Assert.Equal(10, all[0].Ts); // oldest 10 evicted
        Assert.Equal(MetricsStore.Capacity + 9, all[^1].Ts);
    }

    [Fact]
    public void Query_FiltersBySince_AndUnknownNodeEmpty()
    {
        var store = new MetricsStore();
        store.Add("n1", P(1000));
        store.Add("n1", P(2000));
        store.Add("n1", P(3000));
        Assert.Equal(2, store.Query("n1", 2000).Count);
        Assert.Empty(store.Query("ghost"));
    }

    [Fact]
    public void PerNode_Isolation()
    {
        var store = new MetricsStore();
        store.Add("n1", P(1));
        store.Add("n2", P(2));
        Assert.Single(store.Query("n1"));
        Assert.Single(store.Query("n2"));
    }
}

public class MetricsSamplerTests
{
    private static MetricsSampler NewSampler(MetricsStore store,
        Dictionary<string, (string Output, int Exit)> responses, List<string> polled,
        HashSet<string>? throwNodes = null, Func<DateTimeOffset>? now = null)
    {
        throwNodes ??= new HashSet<string>();
        return new MetricsSampler(
            bridge: null!,
            nodes: () => responses.Select(kv => (kv.Key, true)),
            store: store,
            logger: NullLogger<MetricsSampler>.Instance,
            interval: TimeSpan.FromMinutes(1),
            dispatch: (nodeId, args, timeout) =>
            {
                polled.Add(nodeId);
                if (throwNodes.Contains(nodeId)) throw new BridgeException(503, "unreachable");
                var (output, exit) = responses[nodeId];
                return Task.FromResult(new NodeCommandBridge.CommandOutcome(exit, output, "req"));
            },
            now: now);
    }

    private const string SampleJson = """{"cpu_used_pct":42.5,"mem_total_kb":16000000,"mem_used_kb":8000000,"disk_read_bps":1024,"disk_write_bps":2048,"net_rx_total":100,"net_tx_total":200}""";

    [Fact]
    public async Task PollOnce_StoresOnlineNodeSamples()
    {
        var store = new MetricsStore();
        var polled = new List<string>();
        var sampler = NewSampler(store, new Dictionary<string, (string, int)>
        {
            ["n1"] = (SampleJson, 0),
        }, polled);
        await sampler.PollOnce();
        Assert.Equal(new[] { "n1" }, polled);
        var pts = store.Query("n1");
        Assert.Single(pts);
        Assert.Equal(42.5, pts[0].CpuUsedPct);
        Assert.Equal(16000000UL, pts[0].MemTotalKB);
        Assert.Equal(8000000UL, pts[0].MemUsedKB);
        Assert.Equal(1024UL, pts[0].DiskReadBps);
        Assert.Equal(2048UL, pts[0].DiskWriteBps);
        Assert.Equal(100UL, pts[0].NetRxTotal);
        Assert.Equal(200UL, pts[0].NetTxTotal);
    }

    [Fact]
    public async Task PollOnce_SkipsOfflineAndFailuresSilently()
    {
        var store = new MetricsStore();
        var polled = new List<string>();
        var sampler = new MetricsSampler(
            bridge: null!,
            nodes: () => new (string, bool)[] { ("n1", false), ("n2", true), ("n3", true), ("n4", true) },
            store: store,
            logger: NullLogger<MetricsSampler>.Instance,
            interval: TimeSpan.FromMinutes(1),
            dispatch: (nodeId, args, timeout) =>
            {
                polled.Add(nodeId);
                return nodeId switch
                {
                    "n2" => Task.FromResult(new NodeCommandBridge.CommandOutcome(1, "err", "req")),
                    "n3" => throw new BridgeException(503, "unreachable"),
                    "n4" => Task.FromResult(new NodeCommandBridge.CommandOutcome(0, "not-json", "req")),
                    _ => throw new InvalidOperationException(),
                };
            });
        await sampler.PollOnce();
        Assert.Equal(new[] { "n2", "n3", "n4" }, polled); // offline n1 skipped
        Assert.Empty(store.Query("n2")); // exit != 0 → silent
        Assert.Empty(store.Query("n3")); // bridge exception → silent
        Assert.Empty(store.Query("n4")); // bad JSON → silent
    }

    [Fact]
    public async Task PollOnce_UsesInjectedClock()
    {
        var store = new MetricsStore();
        var sampler = NewSampler(store, new Dictionary<string, (string, int)>
        {
            ["n1"] = (SampleJson, 0),
        }, new List<string>(), now: () => DateTimeOffset.FromUnixTimeMilliseconds(5_000_000));
        await sampler.PollOnce();
        Assert.Equal(5_000_000, store.Query("n1")[0].Ts);
    }
}
