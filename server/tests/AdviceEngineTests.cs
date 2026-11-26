using TaoOps.Server.Alerts;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/prd-v14-b3.md §五 FR-A29（AI 运维建议）。</summary>
public class AdviceEngineTests
{
    private const long DayMs = 86_400_000L;

    private sealed class FakeSource : IAdviceDataSource
    {
        public IReadOnlyList<MetricsStore.MetricPoint> Metrics { get; set; } = [];
        public IReadOnlyList<Alert> Alerts { get; set; } = [];
        public IReadOnlyList<CertStatusInfo> Certs { get; set; } = [];
        public int BackupCoverage { get; set; } = -1;
        public int Restarts { get; set; } = -1;

        public IReadOnlyList<MetricsStore.MetricPoint> GetMetrics(string nodeId, long sinceMs) => Metrics;
        public IReadOnlyList<Alert> GetAlertHistory(string nodeId, long sinceMs) => Alerts;
        public IReadOnlyList<CertStatusInfo> GetCertStatus(string nodeId) => Certs;
        public int GetBackupCoverage(string nodeId) => BackupCoverage;
        public Task<int> GetContainerRestarts(string nodeId, CancellationToken ct = default) => Task.FromResult(Restarts);
    }

    private static readonly Settings.AlertThresholdSettings Thresholds = new();

    private static MetricsStore.MetricPoint P(long ts, double cpu = 10, ulong memUsed = 100, ulong memTotal = 1000,
        ulong diskUsed = 0, ulong diskTotal = 0) =>
        new(ts, cpu, memTotal, memUsed, 1, 1, 1, 1, diskUsed, diskTotal);

    private static IReadOnlyList<MetricsStore.MetricPoint> Series(long now, Func<long, MetricsStore.MetricPoint> f, int n)
    {
        var list = new List<MetricsStore.MetricPoint>();
        for (var i = 0; i < n; i++) list.Add(f(now - (n - 1 - i) * 60_000L));
        return list;
    }

    [Fact]
    public async Task DataInsufficient_NoAdvice_NoGuessing()
    {
        // 采样点 <10 → 不出 CPU/内存/磁盘建议（数据不足不臆测）。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource
        {
            Metrics = Series(now, ts => P(ts, cpu: 99), 5),
            BackupCoverage = -1, Restarts = -1,
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.DoesNotContain(advices, a => a.Id == "cpu-peak");
        Assert.DoesNotContain(advices, a => a.Id == "mem-peak");
        Assert.DoesNotContain(advices, a => a.Id == "disk-watermark");
    }

    // ---- 规则 1：CPU/内存峰值（正例 + 反例）----

    [Fact]
    public async Task Rule1_CpuPeak_Triggers()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource { Metrics = Series(now, ts => P(ts, cpu: 95), 12) };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        var a = Assert.Single(advices, x => x.Id == "cpu-peak");
        Assert.Equal("critical", a.Level); // 95 >= 85+10
    }

    [Fact]
    public async Task Rule1_CpuBelowThreshold_NoAdvice()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource { Metrics = Series(now, ts => P(ts, cpu: 50), 12) };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.DoesNotContain(advices, a => a.Id == "cpu-peak");
    }

    [Fact]
    public async Task Rule1_MemPeak_Triggers()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource { Metrics = Series(now, ts => P(ts, memUsed: 900, memTotal: 1000), 12) };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.Contains(advices, a => a.Id == "mem-peak");
    }

    // ---- 规则 2：磁盘水位（快照口径 + 斜率预测口径）----

    [Fact]
    public async Task Rule2_DiskSnapshotOverThreshold_Triggers()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // 12 点（<20，无斜率）但当前用量 92% ≥ 90 → 快照口径触发。
        var src = new FakeSource
        {
            Metrics = Series(now, ts => P(ts, diskUsed: 92, diskTotal: 100), 12),
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        var a = Assert.Single(advices, x => x.Id == "disk-watermark");
        Assert.Contains("不足以预测", a.Basis);
    }

    [Fact]
    public async Task Rule2_DiskSlopePredictsExhaustion_Triggers()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // 30 个点、每小时一个（跨度约 29h > 0.2 天）：用量从 10% 涨到 50%，斜率外推 1 天内破 90%。
        var src = new FakeSource
        {
            Metrics = Enumerable.Range(0, 30).Select(i => P(
                now - (29 - i) * 3_600_000L,
                diskUsed: (ulong)(10 + (double)i / 29 * 40),
                diskTotal: 100)).ToList(),
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        var a = Assert.Single(advices, x => x.Id == "disk-watermark");
        Assert.Contains("预计", a.Basis);
    }

    [Fact]
    public async Task Rule2_DiskHealthy_NoAdvice()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource
        {
            Metrics = Series(now, ts => P(ts, diskUsed: 20, diskTotal: 100), 30),
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.DoesNotContain(advices, a => a.Id == "disk-watermark");
    }

    // ---- 规则 3：证书 30 天内到期 ----

    [Fact]
    public async Task Rule3_CertExpiringSoon_Triggers()
    {
        var src = new FakeSource
        {
            Certs = [new CertStatusInfo("example.com", DateTimeOffset.UtcNow.AddDays(10), false)],
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        var a = Assert.Single(advices, x => x.Id == "cert-example.com");
        Assert.Contains("未配置自动续期", a.Basis);
    }

    [Fact]
    public async Task Rule3_CertHealthy_NoAdvice()
    {
        var src = new FakeSource
        {
            Certs = [new CertStatusInfo("example.com", DateTimeOffset.UtcNow.AddDays(90), true)],
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.DoesNotContain(advices, a => a.Id.StartsWith("cert-"));
    }

    // ---- 规则 4：无备份策略覆盖 ----

    [Fact]
    public async Task Rule4_NoBackupCoverage_Triggers()
    {
        var src = new FakeSource { BackupCoverage = 0 };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.Contains(advices, a => a.Id == "no-backup");
    }

    [Fact]
    public async Task Rule4_BackupCovered_NoAdvice()
    {
        var src = new FakeSource { BackupCoverage = 2 };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.DoesNotContain(advices, a => a.Id == "no-backup");
    }

    // ---- 规则 5：容器重启 ≥3 ----

    [Fact]
    public async Task Rule5_ContainerRestarts_Triggers()
    {
        var src = new FakeSource { Restarts = 5 };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.Contains(advices, a => a.Id == "container-restarts");
    }

    [Fact]
    public async Task Rule5_ContainerRestartsInsufficient_NoAdvice()
    {
        var src = new FakeSource { Restarts = -1 }; // 数据不足
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.DoesNotContain(advices, a => a.Id == "container-restarts");
    }

    // ---- 规则 6：告警风暴 ----

    [Fact]
    public async Task Rule6_AlertStorm_Triggers()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var alerts = Enumerable.Range(0, 6).Select(i => new Alert
        {
            Source = "disk", Level = "warn", NodeId = "n1", Detail = $"d{i}", Ts = now - i * 1000,
        }).ToList();
        var src = new FakeSource { Alerts = alerts };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        var a = Assert.Single(advices, x => x.Id == "alert-storm");
        Assert.Contains("disk", a.Title);
    }

    [Fact]
    public async Task Rule6_FewAlerts_NoAdvice()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource
        {
            Alerts = [new Alert { Source = "disk", NodeId = "n1", Ts = now }],
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.DoesNotContain(advices, a => a.Id == "alert-storm");
    }
}
