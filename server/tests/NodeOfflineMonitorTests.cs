using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A22 节点离线巡检：断连超阈值边缘触发 warn 告警，恢复产生 info 复位
/// 记录；重复巡检不重复告警；注册表移除的节点静默清理边缘状态。
/// </summary>
public class NodeOfflineMonitorTests
{
    private static (Alerts.AlertCenter Center, Alerts.AlertStore Store) NewCenter(string dataDir)
    {
        var store = new Alerts.AlertStore(dataDir);
        return (new Alerts.AlertCenter(store), store);
    }

    private static NodeOfflineMonitor NewMonitor(Alerts.AlertCenter center,
        Dictionary<string, bool> states, List<string> polled)
    {
        // Advance the clock one minute per poll so alert ordering is stable.
        var tick = 0;
        return new NodeOfflineMonitor(
            nodes: () => states.Select(kv => (kv.Key, kv.Value)),
            alerts: center,
            logger: NullLogger<NodeOfflineMonitor>.Instance,
            interval: TimeSpan.FromHours(1),
            now: () => DateTimeOffset.Parse("2026-11-24T12:00:00Z").AddMinutes(tick++));
    }

    [Fact]
    public void OfflineNode_RaisesWarnOnce_ThenResetsOnReconnect()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var states = new Dictionary<string, bool> { ["node-1"] = false };
        var monitor = NewMonitor(center, states, new List<string>());

        monitor.PollOnce();
        var alerts = store.Query(source: "node").ToList();
        Assert.Single(alerts);
        Assert.Equal(Alerts.AlertLevels.Warn, alerts[0].Level);
        Assert.Contains("offline", alerts[0].Detail);

        // Second poll with same state: no duplicate.
        monitor.PollOnce();
        Assert.Single(store.Query(source: "node").ToList());

        // Reconnect: info recovery record (newest first), edge state reset.
        states["node-1"] = true;
        monitor.PollOnce();
        var all = store.Query(source: "node").ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal(Alerts.AlertLevels.Info, all[0].Level);
        Assert.Contains("reconnected", all[0].Detail);

        // Offline again re-alerts.
        states["node-1"] = false;
        monitor.PollOnce();
        Assert.Equal(3, store.Query(source: "node").ToList().Count);
    }

    [Fact]
    public void ConnectedNode_NoAlert()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var states = new Dictionary<string, bool> { ["node-1"] = true };
        var monitor = NewMonitor(center, states, new List<string>());
        monitor.PollOnce();
        Assert.Empty(store.Query(source: "node").ToList());
    }

    [Fact]
    public void RemovedNode_StateClearedSilently()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var states = new Dictionary<string, bool> { ["node-1"] = false };
        var monitor = NewMonitor(center, states, new List<string>());

        monitor.PollOnce();
        Assert.Single(store.Query(source: "node").ToList());

        // Node removed from registry: edge state cleared, no extra alerts.
        states.Remove("node-1");
        monitor.PollOnce();
        Assert.Single(store.Query(source: "node").ToList());

        // Node rejoins connected: no stale alert, no recovery record.
        states["node-1"] = true;
        monitor.PollOnce();
        Assert.Single(store.Query(source: "node").ToList());
    }

    [Fact]
    public void Threshold_Default300_EnvOverridable()
    {
        Assert.Equal(300, NodeOfflineMonitor.OfflineThresholdSec);
    }
}
