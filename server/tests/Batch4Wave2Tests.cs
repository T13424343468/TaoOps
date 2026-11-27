using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 批次四第二波：DefaultAdviceDataSource 全路径（证书/备份覆盖/容器重启/指标/告警
/// 各数据源的成功、失败、空数据、异常分支）与 BackupScheduleMonitor 补充分支
/// （离线跳过、BridgeException、null 策略、空 Id、Due 各解析分支、run 通道异常告警）。
/// </summary>
public class Batch4Wave2Tests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");

    #region DefaultAdviceDataSource

    private static DefaultAdviceDataSource NewDs(Func<string, NodeCommandBridge.CommandOutcome>? respond = null,
        List<string>? calls = null, NodeCommandBridge? bridge = null,
        MetricsStore? metrics = null, Alerts.AlertStore? alerts = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        return new DefaultAdviceDataSource(metrics ?? new MetricsStore(), alerts ?? new Alerts.AlertStore(dir), bridge!,
            respond == null
                ? null
                : (nodeId, args, timeout) =>
                {
                    calls?.Add(args[0]);
                    return Task.FromResult(respond(args[0]));
                });
    }

    private static NodeCommandBridge.CommandOutcome Ok(string output) => new(0, output, "req");
    private static NodeCommandBridge.CommandOutcome Fail(string output = "") => new(1, output, "req");

    [Fact]
    public async Task CertStatus_ExitNonZero_ReturnsEmpty()
    {
        var ds = NewDs(args => args == "op=list" ? Fail() : Ok("null"));
        Assert.Empty(ds.GetCertStatus("n1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CertStatus_ParsesDates_BadDateFallsBackToMinValue()
    {
        var ds = NewDs(args => Ok(
            """[{"domain":"a.com","not_after":"2027-01-02T00:00:00Z"},{"domain":"b.com","not_after":"garbage"}]"""));
        var certs = ds.GetCertStatus("n1");
        Assert.Equal(2, certs.Count);
        Assert.Equal("a.com", certs[0].Name);
        Assert.Equal(DateTimeOffset.Parse("2027-01-02T00:00:00Z"), certs[0].ExpiresAt);
        Assert.Equal(DateTimeOffset.MinValue, certs[1].ExpiresAt);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CertStatus_NullList_ReturnsEmpty()
    {
        var ds = NewDs(args => Ok("null"));
        Assert.Empty(ds.GetCertStatus("n1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CertStatus_DispatchThrows_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var ds = new DefaultAdviceDataSource(new MetricsStore(), new Alerts.AlertStore(dir), null!,
            (_, _, _) => throw new InvalidOperationException("channel down"));
        Assert.Empty(ds.GetCertStatus("n1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CertStatus_DefaultDispatch_NullBridge_DataUnavailable()
    {
        // 未注入 dispatch 时走桥默认通道；桥不可用按「数据不足」处理。
        var ds = NewDs();
        Assert.Empty(ds.GetCertStatus("n1"));
        Assert.Equal(-1, ds.GetBackupCoverage("n1"));
        Assert.Equal(-1, await ds.GetContainerRestarts("n1"));
    }

    [Fact]
    public async Task BackupCoverage_ExitNonZero_ReturnsMinus1()
    {
        var ds = NewDs(args => args == "op=policy_list" ? Fail() : Ok("null"));
        Assert.Equal(-1, ds.GetBackupCoverage("n1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BackupCoverage_CountsEnabledOnly()
    {
        var ds = NewDs(args => Ok("""[{"enabled":true},{"enabled":false},{"enabled":true}]"""));
        Assert.Equal(2, ds.GetBackupCoverage("n1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BackupCoverage_NullList_ReturnsZero()
    {
        var ds = NewDs(args => Ok("null"));
        Assert.Equal(0, ds.GetBackupCoverage("n1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BackupCoverage_DispatchThrows_ReturnsMinus1()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var ds = new DefaultAdviceDataSource(new MetricsStore(), new Alerts.AlertStore(dir), null!,
            (_, _, _) => throw new InvalidOperationException("channel down"));
        Assert.Equal(-1, ds.GetBackupCoverage("n1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ContainerRestarts_ExitNonZero_ReturnsMinus1()
    {
        var ds = NewDs(args => args == "op=inspect" ? Fail() : Ok("null"));
        Assert.Equal(-1, await ds.GetContainerRestarts("n1"));
    }

    [Fact]
    public async Task ContainerRestarts_Success_ReturnsTotal()
    {
        var ds = NewDs(args => Ok("""{"total_restarts":7}"""));
        Assert.Equal(7, await ds.GetContainerRestarts("n1"));
    }

    [Fact]
    public async Task ContainerRestarts_NullView_ReturnsMinus1()
    {
        var ds = NewDs(args => Ok("null"));
        Assert.Equal(-1, await ds.GetContainerRestarts("n1"));
    }

    [Fact]
    public async Task ContainerRestarts_DispatchThrows_ReturnsMinus1()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var ds = new DefaultAdviceDataSource(new MetricsStore(), new Alerts.AlertStore(dir), null!,
            (_, _, _) => throw new InvalidOperationException("node offline"));
        Assert.Equal(-1, await ds.GetContainerRestarts("n1"));
    }

    [Fact]
    public void GetMetrics_FiltersSince()
    {
        var metrics = new MetricsStore();
        metrics.Add("n1", new MetricsStore.MetricPoint(1000, 1, 2, 1, 0, 0, 0, 0));
        metrics.Add("n1", new MetricsStore.MetricPoint(2000, 5, 2, 1, 0, 0, 0, 0));
        var ds = NewDs(metrics: metrics);
        Assert.Equal(2, ds.GetMetrics("n1", 0).Count);
        Assert.Single(ds.GetMetrics("n1", 1500));
        Assert.Empty(ds.GetMetrics("missing", 0));
    }

    [Fact]
    public void GetAlertHistory_FiltersByTs()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var store = new Alerts.AlertStore(dir);
        var center = new Alerts.AlertCenter(store);
        center.Raise("backup", Alerts.AlertLevels.Warn, "n1", "old", tsMs: 1000);
        center.Raise("backup", Alerts.AlertLevels.Warn, "n1", "new", tsMs: 3000);
        var ds = NewDs(alerts: store);
        var history = ds.GetAlertHistory("n1", 2000).ToList();
        Assert.Single(history);
        Assert.Equal("new", history[0].Detail);
        Assert.Empty(ds.GetAlertHistory("missing", 0));
    }

    #endregion

    #region BackupScheduleMonitor 补充分支

    private static BackupScheduleMonitor NewMonitor(Func<string, NodeCommandBridge.CommandOutcome> respond,
        List<string>? calls = null, bool connected = true, Func<DateTimeOffset>? now = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var store = new Alerts.AlertStore(dir);
        var center = new Alerts.AlertCenter(store);
        return new BackupScheduleMonitor(
            bridge: null!,
            nodes: () => new[] { ("node-1", connected) },
            alerts: center,
            logger: NullLogger<BackupScheduleMonitor>.Instance,
            interval: TimeSpan.FromMinutes(1),
            dispatch: (nodeId, args, timeout) =>
            {
                calls?.Add(args[0]);
                return Task.FromResult(respond(args[0]));
            },
            now: now ?? (() => Now));
    }

    private static string Policies(string id, string schedule, long lastRunMs, bool enabled = true) =>
        $$"""[{"id":"{{id}}","name":"site","source":"/www","type":"full","schedule":"{{schedule}}","retain":7,"enabled":{{enabled.ToString().ToLowerInvariant()}},"last_run_ms":{{lastRunMs}},"last_result":"ok"}]""";

    [Fact]
    public async Task OfflineNode_Skipped()
    {
        var calls = new List<string>();
        var monitor = NewMonitor(args => Ok("[]"), calls, connected: false);
        await monitor.PollOnce();
        Assert.Empty(calls);
    }

    [Fact]
    public async Task BridgeExceptionOnList_SkippedQuietly()
    {
        var monitor = NewMonitor(args => throw new BridgeException(503, "node unreachable"));
        await monitor.PollOnce(); // 不抛出即通过
    }

    [Fact]
    public async Task NullPolicies_Skipped()
    {
        var calls = new List<string>();
        var monitor = NewMonitor(args => Ok("null"), calls);
        await monitor.PollOnce();
        Assert.Single(calls); // 仅 policy_list，无 policy_run
    }

    [Fact]
    public async Task EmptyIdPolicy_Skipped()
    {
        var calls = new List<string>();
        var monitor = NewMonitor(args => Ok(Policies("", "interval:60", 0)), calls);
        await monitor.PollOnce();
        Assert.Single(calls);
    }

    [Fact]
    public async Task RunBridgeException_RaisesAlertWithDetail_ThenNoDuplicate()
    {
        var calls = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var store = new Alerts.AlertStore(dir);
        var center = new Alerts.AlertCenter(store);
        var monitor = new BackupScheduleMonitor(
            bridge: null!,
            nodes: () => new[] { ("node-1", true) },
            alerts: center,
            logger: NullLogger<BackupScheduleMonitor>.Instance,
            interval: TimeSpan.FromMinutes(1),
            dispatch: (nodeId, args, timeout) =>
                args[0] == "op=policy_run"
                    ? throw new BridgeException(504, "run channel timeout")
                    : Task.FromResult(Ok(Policies("bp-9", "interval:60", 0))),
            now: () => Now);

        await monitor.PollOnce();
        var alerts = store.Query(source: "backup").ToList();
        Assert.Single(alerts);
        Assert.Contains("run channel timeout", alerts[0].Detail);

        await monitor.PollOnce();
        Assert.Single(store.Query(source: "backup").ToList());
    }

    [Theory]
    [InlineData("interval:abc")]   // 解析失败
    [InlineData("interval:30")]    // 间隔小于 60s
    [InlineData("weekly:sunday")]  // 未知前缀
    [InlineData("daily:3")]        // 段数不足
    [InlineData("daily:ab:00")]    // 小时非数字
    [InlineData("daily:24:00")]    // 小时越界
    [InlineData("daily:03:60")]    // 分钟越界
    public async Task InvalidSchedules_NeverDue(string schedule)
    {
        var calls = new List<string>();
        var monitor = NewMonitor(args => Ok(Policies("bp-1", schedule, 0)), calls);
        await monitor.PollOnce();
        Assert.Single(calls); // 仅 policy_list
    }

    [Fact]
    public async Task Daily_AlreadyRanToday_NotDue()
    {
        var today = Now.ToUnixTimeMilliseconds();
        var calls = new List<string>();
        var monitor = NewMonitor(args => Ok(Policies("bp-1", "daily:03:00", today)), calls);
        await monitor.PollOnce();
        Assert.Single(calls);
    }

    [Fact]
    public async Task Daily_BeforePlannedTime_NotDue()
    {
        var calls = new List<string>();
        var monitor = NewMonitor(args => Ok(Policies("bp-1", "daily:13:00", 0)), calls, now: () => Now);
        await monitor.PollOnce();
        Assert.Single(calls);
    }

    [Fact]
    public async Task Daily_ExactlyAtPlannedTime_Due()
    {
        var calls = new List<string>();
        var monitor = NewMonitor(args => Ok(Policies("bp-1", "daily:12:00", 0)), calls, now: () => Now);
        await monitor.PollOnce();
        Assert.Contains("op=policy_run", calls);
    }

    #endregion
}
