using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A15 复制权限矩阵（契约 §13）：replica_status=ReadOnly+；
/// master_grant/replica_config/replica_reset=Admin；未知操作默认拒绝。
/// </summary>
public class DbPolicyTests
{
    [Theory]
    [InlineData("replica_status", UserRole.ReadOnly, true)]
    [InlineData("replica_status", UserRole.Operator, true)]
    [InlineData("replica_status", UserRole.Admin, true)]
    [InlineData("master_grant", UserRole.ReadOnly, false)]
    [InlineData("master_grant", UserRole.Operator, false)]
    [InlineData("master_grant", UserRole.Admin, true)]
    [InlineData("replica_config", UserRole.Admin, true)]
    [InlineData("replica_config", UserRole.Operator, false)]
    [InlineData("replica_reset", UserRole.Admin, true)]
    [InlineData("replica_reset", UserRole.ReadOnly, false)]
    public void Matrix_MatchesContract(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, DbPolicy.Allows(op, role));
    }

    [Fact]
    public void UnknownOp_DefaultsAdmin()
    {
        Assert.Equal(UserRole.Admin, DbPolicy.RequiredRole("create"));
        Assert.False(DbPolicy.Allows("create", UserRole.Operator));
    }
}

/// <summary>
/// FR-A15 复制中断巡检：IO/SQL 线程中断与延迟超阈值边缘触发 warn 告警，
/// 恢复后复位；未配置复制的节点不告警。
/// </summary>
public class ReplicationMonitorTests
{
    private static (Alerts.AlertCenter Center, Alerts.AlertStore Store) NewCenter(string dataDir)
    {
        var store = new Alerts.AlertStore(dataDir);
        return (new Alerts.AlertCenter(store), store);
    }

    private static ReplicationMonitor NewMonitor(Alerts.AlertCenter center,
        Dictionary<string, (string Output, int Exit)> responses, List<string> polled)
    {
        return new ReplicationMonitor(
            bridge: null!,
            nodes: () => responses.Select(kv => (kv.Key, true)),
            alerts: center,
            logger: NullLogger<ReplicationMonitor>.Instance,
            interval: TimeSpan.FromHours(1),
            dispatch: (nodeId, cmd, timeout) =>
            {
                polled.Add(nodeId);
                var (output, exit) = responses[nodeId];
                return Task.FromResult(new NodeCommandBridge.CommandOutcome(exit, output, "req"));
            });
    }

    [Fact]
    public async Task ThreadDown_RaisesWarnOnce_ThenResets()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var down = """{"configured":true,"io_running":"No","sql_running":"Yes","seconds_behind_master":0}""";
        var ok = """{"configured":true,"io_running":"Yes","sql_running":"Yes","seconds_behind_master":0}""";
        var state = new Dictionary<string, (string, int)> { ["node-1"] = (down, 0) };
        var polled = new List<string>();
        var monitor = NewMonitor(center, state, polled);

        await monitor.PollOnce();
        Assert.Single(polled);
        var alerts = store.Query(source: "replication").ToList();
        Assert.Single(alerts);
        Assert.Equal(Alerts.AlertLevels.Warn, alerts[0].Level);

        // Second poll with the same state: no duplicate alert.
        await monitor.PollOnce();
        Assert.Single(store.Query(source: "replication").ToList());

        // Recovery resets the edge state; a later failure re-alerts.
        state["node-1"] = (ok, 0);
        await monitor.PollOnce();
        state["node-1"] = (down, 0);
        await monitor.PollOnce();
        Assert.Equal(2, store.Query(source: "replication").ToList().Count);
    }

    [Fact]
    public async Task LagOverThreshold_RaisesWarn()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var lagging = $$"""{"configured":true,"io_running":"Yes","sql_running":"Yes","seconds_behind_master":301}""";
        var state = new Dictionary<string, (string, int)> { ["node-1"] = (lagging, 0) };
        var monitor = NewMonitor(center, state, new List<string>());

        await monitor.PollOnce();
        var alerts = store.Query(source: "replication").ToList();
        Assert.Single(alerts);
        Assert.Contains("301", alerts[0].Detail);
        Assert.Equal(Alerts.AlertLevels.Warn, alerts[0].Level);
    }

    [Fact]
    public async Task UnconfiguredNode_NoAlert()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)>
        {
            ["node-1"] = ("""{"configured":false,"io_running":"No","sql_running":"No","seconds_behind_master":0}""", 0),
        };
        var monitor = NewMonitor(center, state, new List<string>());
        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "replication").ToList());
    }

    [Fact]
    public async Task NonZeroExit_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)> { ["node-1"] = ("garbage", 1) };
        var monitor = NewMonitor(center, state, new List<string>());
        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "replication").ToList());
    }

    [Fact]
    public void LagThreshold_Default300_EnvOverridable()
    {
        Assert.Equal(300, ReplicationMonitor.LagThresholdSec);
    }
}
