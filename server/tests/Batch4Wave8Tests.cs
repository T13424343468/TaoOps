using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次四第八波：五个后台监控 ExecuteAsync 循环体（短周期 + 取消收尾）、
/// SslMonitor/ReplicationMonitor 的桥接异常与非零退出与恢复复位臂、
/// BackupScheduleMonitor PolicyView.Due 非法 interval 臂、NodeOfflineMonitor 阈值回退臂。
/// </summary>
public class Batch4Wave8Tests
{
    private static (Alerts.AlertCenter Center, Alerts.AlertStore Store) NewCenter(string dataDir)
    {
        var store = new Alerts.AlertStore(dataDir);
        return (new Alerts.AlertCenter(store), store);
    }

    /// <summary>节点清单函数：首轮空、次轮通用异常（catch Exception 臂）、三轮 OCE（catch OCE 重抛臂）。</summary>
    private static Func<IEnumerable<(string NodeId, bool Connected)>> FlakyNodes()
    {
        var calls = 0;
        return () =>
        {
            if (++calls >= 3) throw new OperationCanceledException("node list canceled");
            if (calls == 2) throw new InvalidOperationException("node list exploded");
            return [];
        };
    }

    /// <summary>短周期启动后台服务，跑数轮循环后取消收尾（覆盖循环体/回边/延迟 await）。</summary>
    private static async Task PumpLoopAsync(BackgroundService svc)
    {
        using var cts = new CancellationTokenSource();
        _ = svc.StartAsync(cts.Token);
        await Task.Delay(250);
        try { await svc.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }
    }

    // ---- 五个后台监控：ExecuteAsync 循环体 ----

    [Fact]
    public async Task MetricsSampler_Loop_RunsAndSurvivesPollErrors()
    {
        var sampler = new MetricsSampler(null!, FlakyNodes(), new MetricsStore(),
            NullLogger<MetricsSampler>.Instance, interval: TimeSpan.FromMilliseconds(30));
        await PumpLoopAsync(sampler);
    }

    [Fact]
    public async Task NodeOfflineMonitor_Loop_RunsAndSurvivesPollErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, _) = NewCenter(dir);
            var monitor = new NodeOfflineMonitor(FlakyNodes(), center,
                NullLogger<NodeOfflineMonitor>.Instance, interval: TimeSpan.FromMilliseconds(30));
            await PumpLoopAsync(monitor);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task BackupScheduleMonitor_Loop_RunsAndSurvivesPollErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, _) = NewCenter(dir);
            var monitor = new BackupScheduleMonitor(null!, FlakyNodes(), center,
                NullLogger<BackupScheduleMonitor>.Instance, interval: TimeSpan.FromMilliseconds(30));
            await PumpLoopAsync(monitor);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task SslMonitor_Loop_RunsAndSurvivesPollErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, _) = NewCenter(dir);
            var monitor = new SslMonitor(null!, FlakyNodes(), center,
                NullLogger<SslMonitor>.Instance, interval: TimeSpan.FromMilliseconds(30));
            await PumpLoopAsync(monitor);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ReplicationMonitor_Loop_RunsAndSurvivesPollErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, _) = NewCenter(dir);
            var monitor = new ReplicationMonitor(null!, FlakyNodes(), center,
                NullLogger<ReplicationMonitor>.Instance, interval: TimeSpan.FromMilliseconds(30));
            await PumpLoopAsync(monitor);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- SslMonitor PollOnce：桥接异常/非零退出/非法证书字段/健康证书/续期成功复位臂 ----

    private static SslMonitor NewSslMonitor(Alerts.AlertCenter center,
        Dictionary<string, (string Output, int Exit)> listResponses,
        Dictionary<string, int> renewExits, List<string> runCalls, Func<DateTimeOffset>? now = null) =>
        new(
            bridge: null!,
            nodes: () => listResponses.Select(kv => (kv.Key, true)),
            alerts: center,
            logger: NullLogger<SslMonitor>.Instance,
            interval: TimeSpan.FromHours(1),
            dispatch: (nodeId, args, timeout) =>
            {
                if (args.Length > 0 && args[0] == "op=renew")
                {
                    if (nodeId == "renew-dead") throw new BridgeException(503, "unreachable during renew");
                    runCalls.Add(nodeId);
                    var domain = args.FirstOrDefault(a => a.StartsWith("domain=")) ?? "";
                    renewExits.TryGetValue(nodeId + domain, out var exit);
                    return Task.FromResult(new NodeCommandBridge.CommandOutcome(exit, "renewed", "req"));
                }
                if (nodeId == "bridge-dead") throw new BridgeException(503, "unreachable");
                var (output, exitCode) = listResponses[nodeId];
                return Task.FromResult(new NodeCommandBridge.CommandOutcome(exitCode, output, "req"));
            },
            now: now ?? (() => DateTimeOffset.Parse("2026-11-24T12:00:00Z")));

    private static string Certs(params (string Domain, string NotAfter)[] items) =>
        JsonSerializer.Serialize(items.Select(i => new { domain = i.Domain, cert_path = "/x.crt", issuer = "le", not_after = i.NotAfter }));

    [Fact]
    public async Task Ssl_PollOnce_BridgeExceptionAndNonZeroExit_SkipSilently()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, store) = NewCenter(dir);
            var monitor = NewSslMonitor(center, new Dictionary<string, (string, int)>
            {
                ["bridge-dead"] = ("[]", 0),      // dispatch 抛 BridgeException → skip
                ["exit-one"] = (Certs(("a.com", "2026-12-04T00:00:00Z")), 1), // exit != 0 → skip
            }, new Dictionary<string, int>(), new List<string>());
            await monitor.PollOnce();
            Assert.Empty(store.Query(source: "ssl"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Ssl_PollOnce_EmptyDomainOrBadDate_Skips()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, store) = NewCenter(dir);
            var monitor = NewSslMonitor(center, new Dictionary<string, (string, int)>
            {
                ["n1"] = (Certs(("", "2026-12-04T00:00:00Z"), ("x.com", "not-a-date")), 0),
            }, new Dictionary<string, int>(), new List<string>());
            await monitor.PollOnce();
            Assert.Empty(store.Query(source: "ssl"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Ssl_PollOnce_HealthyCert_NoAlert_RenewSuccessAfterAlert_Resets()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, store) = NewCenter(dir);
            var runCalls = new List<string>();
            var renewExits = new Dictionary<string, int> { ["n1domain=exp.com"] = 1 };
            var monitor = NewSslMonitor(center, new Dictionary<string, (string, int)>
            {
                // healthy.com 一年后到期 → expiring false 臂，不动作。
                // exp.com 10 天内 → 到期窗口；首轮续期失败告警，次轮续期成功 → renewed && alerted 复位臂。
                ["n1"] = (Certs(("healthy.com", "2027-11-24T00:00:00Z"), ("exp.com", "2026-12-04T00:00:00Z")), 0),
            }, renewExits, runCalls);

            await monitor.PollOnce();
            Assert.Single(store.Query(source: "ssl")); // 仅 exp.com 告警

            renewExits["n1domain=exp.com"] = 0; // 续期成功
            await monitor.PollOnce();
            Assert.Single(store.Query(source: "ssl")); // 复位不重复告警

            renewExits["n1domain=exp.com"] = 1; // 再次失败 → 边缘已复位，重新告警
            await monitor.PollOnce();
            Assert.Equal(2, store.Query(source: "ssl").Count());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Ssl_PollOnce_RenewDispatchThrowsBridgeException_Alerts()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, store) = NewCenter(dir);
            var monitor = NewSslMonitor(center, new Dictionary<string, (string, int)>
            {
                // renew dispatch 抛 BridgeException → renewed=false 臂 → 告警。
                ["renew-dead"] = (Certs(("exp.com", "2026-12-04T00:00:00Z")), 0),
            }, new Dictionary<string, int>(), new List<string>());
            await monitor.PollOnce();
            var alerts = store.Query(source: "ssl").ToList();
            Assert.Single(alerts);
            Assert.Contains("exp.com", alerts[0].Detail);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Ssl_PollOnce_CertRenewedToFarFuture_ResetsAlertedState()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, store) = NewCenter(dir);
            var responses = new Dictionary<string, (string, int)>
            {
                ["n2"] = (Certs(("exp.com", "2026-12-04T00:00:00Z")), 0),
            };
            var renewExits = new Dictionary<string, int> { ["n2domain=exp.com"] = 1 };
            var monitor = NewSslMonitor(center, responses, renewExits, new List<string>());

            await monitor.PollOnce(); // 续期失败 → 告警
            Assert.Single(store.Query(source: "ssl"));

            // 证书更新为一年后 → !expiring 且 alerted=true → line 94 复位臂。
            responses["n2"] = (Certs(("exp.com", "2027-11-24T00:00:00Z")), 0);
            await monitor.PollOnce();
            Assert.Single(store.Query(source: "ssl")); // 复位不重复告警
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- ReplicationMonitor PollOnce：桥接异常/非零退出/滞后恢复复位臂 ----

    private static ReplicationMonitor NewReplicationMonitor(Alerts.AlertCenter center,
        Dictionary<string, (string Output, int Exit)> responses) =>
        new(
            bridge: null!,
            nodes: () => responses.Select(kv => (kv.Key, true)),
            alerts: center,
            logger: NullLogger<ReplicationMonitor>.Instance,
            interval: TimeSpan.FromHours(1),
            dispatch: (nodeId, cmd, timeout) =>
            {
                if (nodeId == "bridge-dead") throw new BridgeException(503, "unreachable");
                var (output, exit) = responses[nodeId];
                return Task.FromResult(new NodeCommandBridge.CommandOutcome(exit, output, "req"));
            });

    [Fact]
    public async Task Replication_BridgeExceptionAndNonZeroExit_SkipSilently()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, store) = NewCenter(dir);
            var monitor = NewReplicationMonitor(center, new Dictionary<string, (string, int)>
            {
                ["bridge-dead"] = ("{}", 0),
                ["exit-one"] = ("""{"configured":true,"io_running":"No","sql_running":"No","seconds_behind_master":999}""", 1),
            });
            await monitor.PollOnce();
            Assert.Empty(store.Query(source: "replication"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Replication_LagAlert_ThenRecoveryResets()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, store) = NewCenter(dir);
            var lagging = """{"configured":true,"io_running":"Yes","sql_running":"Yes","seconds_behind_master":99999}""";
            var caught = """{"configured":true,"io_running":"Yes","sql_running":"Yes","seconds_behind_master":0}""";
            var state = new Dictionary<string, (string, int)> { ["n1"] = (lagging, 0) };
            var monitor = NewReplicationMonitor(center, state);

            await monitor.PollOnce();
            var alerts = store.Query(source: "replication").ToList();
            Assert.Single(alerts); // 线程正常，仅滞后告警
            Assert.Contains("lag", alerts[0].Detail);

            state["n1"] = (caught, 0); // 滞后恢复 → lagAlerted 复位臂
            await monitor.PollOnce();
            Assert.Single(store.Query(source: "replication").ToList());

            state["n1"] = (lagging, 0); // 复位后再次滞后 → 重新告警
            await monitor.PollOnce();
            Assert.Equal(2, store.Query(source: "replication").Count());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- BackupScheduleMonitor：PolicyView.Due 非法 interval 臂（经 PollOnce 走真实管道） ----

    private static BackupScheduleMonitor NewBackupMonitor(Alerts.AlertCenter center,
        Dictionary<string, (string Output, int Exit)> listResponses, List<string> runCalls) =>
        new(
            bridge: null!,
            nodes: () => listResponses.Select(kv => (kv.Key, true)),
            alerts: center,
            logger: NullLogger<BackupScheduleMonitor>.Instance,
            interval: TimeSpan.FromHours(1),
            dispatch: (nodeId, args, timeout) =>
            {
                if (args.Length > 0 && args[0] == "op=policy_run")
                {
                    runCalls.Add(args.FirstOrDefault(a => a.StartsWith("id=")) ?? "");
                    return Task.FromResult(new NodeCommandBridge.CommandOutcome(0, "ok", "req"));
                }
                var (output, exitCode) = listResponses[nodeId];
                return Task.FromResult(new NodeCommandBridge.CommandOutcome(exitCode, output, "req"));
            },
            now: () => DateTimeOffset.Parse("2026-11-24T12:00:00Z"));

    private static string Policies(params (string Id, string Schedule)[] items) =>
        JsonSerializer.Serialize(items.Select(i => new
        {
            id = i.Id, name = "site", source = "/www", type = "full",
            schedule = i.Schedule, retain = 7, enabled = true, last_run_ms = 0, last_result = "ok",
        }));

    [Fact]
    public async Task Backup_InvalidOrShortInterval_NotDue_NoRun()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var (center, store) = NewCenter(dir);
            var runCalls = new List<string>();
            var monitor = NewBackupMonitor(center, new Dictionary<string, (string, int)>
            {
                // interval:abc → TryParse false 臂；interval:30 → sec<60 臂；均不到期。
                ["n1"] = (Policies(("bp-a", "interval:abc"), ("bp-b", "interval:30")), 0),
            }, runCalls);
            await monitor.PollOnce();
            Assert.Empty(runCalls);
            Assert.Empty(store.Query(source: "backup"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- NodeOfflineMonitor：阈值 env 非法/零值回退 300 臂 ----

    [Fact]
    public void OfflineThreshold_EnvZeroOrInvalid_FallsBackTo300()
    {
        Environment.SetEnvironmentVariable("TAOOPS_NODE_OFFLINE_THRESHOLD", "0");
        try
        {
            Assert.Equal(300, NodeOfflineMonitor.OfflineThresholdSec); // v>0 false 臂
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_NODE_OFFLINE_THRESHOLD", null);
        }
        Assert.Equal(300, NodeOfflineMonitor.OfflineThresholdSec); // env 缺省 → TryParse false 臂
    }
}
