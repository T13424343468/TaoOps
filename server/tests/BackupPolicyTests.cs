using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A26 备份策略权限矩阵：policy_list=ReadOnly+；policy_run=Operator+；
/// policy_add/policy_remove=Admin；未知操作默认拒绝。
/// </summary>
public class BackupPolicyOpsTests
{
    [Theory]
    [InlineData("policy_list", UserRole.ReadOnly, true)]
    [InlineData("policy_list", UserRole.Operator, true)]
    [InlineData("policy_run", UserRole.ReadOnly, false)]
    [InlineData("policy_run", UserRole.Operator, true)]
    [InlineData("policy_run", UserRole.Admin, true)]
    [InlineData("policy_add", UserRole.Operator, false)]
    [InlineData("policy_add", UserRole.Admin, true)]
    [InlineData("policy_remove", UserRole.Admin, true)]
    [InlineData("policy_remove", UserRole.Operator, false)]
    public void Matrix_MatchesContract(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, BackupPolicyOps.Allows(op, role));
    }

    [Fact]
    public void UnknownOp_DefaultsAdmin()
    {
        Assert.Equal(UserRole.Admin, BackupPolicyOps.RequiredRole("create"));
        Assert.False(BackupPolicyOps.Allows("create", UserRole.Operator));
    }

    [Fact]
    public void Args_FlattensRequestFields()
    {
        var req = new BackupPolicyRequest("policy_add", null, "site", "/www", "full", "daily:03:00", 7, 30_000);
        var args = BackupPolicyArgs.Build(req);
        Assert.Contains("op=policy_add", args);
        Assert.Contains("name=site", args);
        Assert.Contains("source=/www", args);
        Assert.Contains("type=full", args);
        Assert.Contains("schedule=daily:03:00", args);
        Assert.Contains("retain=7", args);
        Assert.Equal(6, args.Count);
    }
}

/// <summary>
/// FR-A26 备份策略调度巡检：到期策略触发执行，失败产生 warn 告警（边缘
/// 触发，成功后复位）；未到期不动作；离线节点跳过。
/// </summary>
public class BackupScheduleMonitorTests
{
    private static (Alerts.AlertCenter Center, Alerts.AlertStore Store) NewCenter(string dataDir)
    {
        var store = new Alerts.AlertStore(dataDir);
        return (new Alerts.AlertCenter(store), store);
    }

    private static BackupScheduleMonitor NewMonitor(Alerts.AlertCenter center,
        Dictionary<string, (string Output, int Exit)> listResponses,
        Dictionary<string, int> runExits, List<string> polled, Func<DateTimeOffset>? now = null)
    {
        return new BackupScheduleMonitor(
            bridge: null!,
            nodes: () => listResponses.Select(kv => (kv.Key, true)),
            alerts: center,
            logger: NullLogger<BackupScheduleMonitor>.Instance,
            interval: TimeSpan.FromMinutes(1),
            dispatch: (nodeId, args, timeout) =>
            {
                polled.Add(nodeId);
                if (args.Length > 0 && args[0] == "op=policy_run")
                {
                    var id = args.FirstOrDefault(a => a.StartsWith("id=")) ?? "";
                    runExits.TryGetValue(nodeId + id, out var exit);
                    return Task.FromResult(new NodeCommandBridge.CommandOutcome(exit, exit == 0 ? "ok" : "failed", "req"));
                }
                var (output, exitCode) = listResponses[nodeId];
                return Task.FromResult(new NodeCommandBridge.CommandOutcome(exitCode, output, "req"));
            },
            now: now ?? (() => DateTimeOffset.Parse("2026-11-24T12:00:00Z")));
    }

    private static string Policies(string id, string schedule, long lastRunMs, bool enabled = true) =>
        $$"""[{"id":"{{id}}","name":"site","source":"/www","type":"full","schedule":"{{schedule}}","retain":7,"enabled":{{enabled.ToString().ToLowerInvariant()}},"last_run_ms":{{lastRunMs}},"last_result":"ok"}]""";

    [Fact]
    public async Task DuePolicy_RunFails_RaisesWarnOnce_ThenResets()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        // interval:3600, last run 2h ago → due.
        var state = new Dictionary<string, (string, int)>
        {
            ["node-1"] = (Policies("bp-1", "interval:3600", 0), 0),
        };
        var runExits = new Dictionary<string, int> { ["node-1id=bp-1"] = 1 }; // run fails
        var polled = new List<string>();
        var monitor = NewMonitor(center, state, runExits, polled);

        await monitor.PollOnce();
        var alerts = store.Query(source: "backup").ToList();
        Assert.Single(alerts);
        Assert.Equal(Alerts.AlertLevels.Warn, alerts[0].Level);
        Assert.Contains("site", alerts[0].Detail);

        // Second poll: no duplicate alert (edge-triggered).
        await monitor.PollOnce();
        Assert.Single(store.Query(source: "backup").ToList());

        // Run succeeds → edge state resets; a later failure re-alerts.
        runExits["node-1id=bp-1"] = 0;
        await monitor.PollOnce();
        runExits["node-1id=bp-1"] = 1;
        await monitor.PollOnce();
        Assert.Equal(2, store.Query(source: "backup").ToList().Count);
    }

    [Fact]
    public async Task DuePolicy_RunSucceeds_NoAlert()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)>
        {
            ["node-1"] = (Policies("bp-1", "daily:03:00", 0), 0),
        };
        var runExits = new Dictionary<string, int> { ["node-1id=bp-1"] = 0 };
        var monitor = NewMonitor(center, state, runExits, new List<string>());

        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "backup").ToList());
    }

    [Fact]
    public async Task NotDue_NoRun()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        // interval:3600, last run 5 min ago → not due.
        var last = DateTimeOffset.Parse("2026-11-24T11:55:00Z").ToUnixTimeMilliseconds();
        var state = new Dictionary<string, (string, int)>
        {
            ["node-1"] = (Policies("bp-1", "interval:3600", last), 0),
        };
        var polled = new List<string>();
        var monitor = NewMonitor(center, state, new Dictionary<string, int>(), polled);

        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "backup").ToList());
        // Only the policy_list dispatch ran; no policy_run.
        Assert.Single(polled);
    }

    [Fact]
    public async Task DisabledPolicy_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)>
        {
            ["node-1"] = (Policies("bp-1", "interval:60", 0, enabled: false), 0),
        };
        var polled = new List<string>();
        var monitor = NewMonitor(center, state, new Dictionary<string, int>(), polled);

        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "backup").ToList());
        Assert.Single(polled);
    }

    [Fact]
    public async Task NonZeroListExit_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)> { ["node-1"] = ("garbage", 1) };
        var monitor = NewMonitor(center, state, new Dictionary<string, int>(), new List<string>());
        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "backup").ToList());
    }

    [Fact]
    public async Task MalformedJson_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)> { ["node-1"] = ("not json", 0) };
        var monitor = NewMonitor(center, state, new Dictionary<string, int>(), new List<string>());
        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "backup").ToList());
    }

    [Fact]
    public void PollInterval_Is60Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), BackupScheduleMonitor.Interval);
    }
}
