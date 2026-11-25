using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A21 SSL 权限矩阵（契约 §14）：list/precheck=ReadOnly+；
/// issue/renew/upload/selfsign=Operator+；未知操作默认拒绝。
/// </summary>
public class SslPolicyTests
{
    [Theory]
    [InlineData("list", UserRole.ReadOnly, true)]
    [InlineData("list", UserRole.Operator, true)]
    [InlineData("list", UserRole.Admin, true)]
    [InlineData("precheck", UserRole.ReadOnly, true)]
    [InlineData("issue", UserRole.ReadOnly, false)]
    [InlineData("issue", UserRole.Operator, true)]
    [InlineData("issue", UserRole.Admin, true)]
    [InlineData("renew", UserRole.Operator, true)]
    [InlineData("renew", UserRole.ReadOnly, false)]
    [InlineData("upload", UserRole.Operator, true)]
    [InlineData("upload", UserRole.ReadOnly, false)]
    [InlineData("selfsign", UserRole.Operator, true)]
    [InlineData("selfsign", UserRole.ReadOnly, false)]
    public void Matrix_MatchesContract(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, SslPolicy.Allows(op, role));
    }

    [Fact]
    public void UnknownOp_DefaultsAdmin()
    {
        Assert.Equal(UserRole.Admin, SslPolicy.RequiredRole("revoke"));
        Assert.False(SslPolicy.Allows("revoke", UserRole.Operator));
    }

    [Fact]
    public void Args_FlattensRequestFields()
    {
        var req = new SslOpRequest("issue", "example.com", "a@b.c", "/var/www", null, null, null, 30_000);
        var args = SslOpArgs.Build(req);
        Assert.Contains("op=issue", args);
        Assert.Contains("domain=example.com", args);
        Assert.Contains("email=a@b.c", args);
        Assert.Contains("webroot=/var/www", args);
        Assert.Equal(4, args.Count);
    }
}

/// <summary>
/// FR-A21 证书到期巡检：到期前 30 天触发续期，续期失败产生 warn 告警
/// （边缘触发，续期成功或证书更新后复位）；未到期不动作。
/// </summary>
public class SslMonitorTests
{
    private static (Alerts.AlertCenter Center, Alerts.AlertStore Store) NewCenter(string dataDir)
    {
        var store = new Alerts.AlertStore(dataDir);
        return (new Alerts.AlertCenter(store), store);
    }

    private static SslMonitor NewMonitor(Alerts.AlertCenter center,
        Dictionary<string, (string Output, int Exit)> listResponses,
        Dictionary<string, int> renewExits, List<string> polled, Func<DateTimeOffset>? now = null)
    {
        return new SslMonitor(
            bridge: null!,
            nodes: () => listResponses.Select(kv => (kv.Key, true)),
            alerts: center,
            logger: NullLogger<SslMonitor>.Instance,
            interval: TimeSpan.FromHours(1),
            dispatch: (nodeId, args, timeout) =>
            {
                polled.Add(nodeId);
                if (args.Length > 0 && args[0] == "op=renew")
                {
                    var domain = args.FirstOrDefault(a => a.StartsWith("domain=")) ?? "";
                    renewExits.TryGetValue(nodeId + domain, out var exit);
                    return Task.FromResult(new NodeCommandBridge.CommandOutcome(exit, "renewed", "req"));
                }
                var (output, exitCode) = listResponses[nodeId];
                return Task.FromResult(new NodeCommandBridge.CommandOutcome(exitCode, output, "req"));
            },
            now: now ?? (() => DateTimeOffset.Parse("2026-11-24T12:00:00Z")));
    }

    private static string Certs(string domain, string notAfter) =>
        $$"""[{"domain":"{{domain}}","cert_path":"/x.crt","issuer":"letsencrypt","not_after":"{{notAfter}}"}]""";

    [Fact]
    public async Task ExpiringCert_RenewFails_RaisesWarnOnce_ThenResets()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        // Cert expires in 10 days → within the 30-day window.
        var state = new Dictionary<string, (string, int)> { ["node-1"] = (Certs("example.com", "2026-12-04T00:00:00Z"), 0) };
        var renewExits = new Dictionary<string, int> { ["node-1domain=example.com"] = 1 }; // renew fails
        var polled = new List<string>();
        var monitor = NewMonitor(center, state, renewExits, polled);

        await monitor.PollOnce();
        var alerts = store.Query(source: "ssl").ToList();
        Assert.Single(alerts);
        Assert.Equal(Alerts.AlertLevels.Warn, alerts[0].Level);
        Assert.Contains("example.com", alerts[0].Detail);

        // Second poll with the same state: no duplicate alert.
        await monitor.PollOnce();
        Assert.Single(store.Query(source: "ssl").ToList());

        // Renewal succeeds → edge state resets; a later failure re-alerts.
        renewExits["node-1domain=example.com"] = 0;
        await monitor.PollOnce();
        renewExits["node-1domain=example.com"] = 1;
        await monitor.PollOnce();
        Assert.Equal(2, store.Query(source: "ssl").ToList().Count);
    }

    [Fact]
    public async Task FarFromExpiry_NoRenewNoAlert()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)> { ["node-1"] = (Certs("example.com", "2027-11-20T00:00:00Z"), 0) };
        var polled = new List<string>();
        var monitor = NewMonitor(center, state, new Dictionary<string, int>(), polled);

        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "ssl").ToList());
        // Only the list dispatch ran; no renew.
        Assert.Single(polled);
    }

    [Fact]
    public async Task RenewSucceeds_NoAlert()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)> { ["node-1"] = (Certs("example.com", "2026-12-04T00:00:00Z"), 0) };
        var renewExits = new Dictionary<string, int> { ["node-1domain=example.com"] = 0 };
        var monitor = NewMonitor(center, state, renewExits, new List<string>());

        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "ssl").ToList());
    }

    [Fact]
    public async Task NonZeroListExit_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)> { ["node-1"] = ("garbage", 1) };
        var monitor = NewMonitor(center, state, new Dictionary<string, int>(), new List<string>());
        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "ssl").ToList());
    }

    [Fact]
    public async Task MalformedJson_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var (center, store) = NewCenter(dir);
        var state = new Dictionary<string, (string, int)> { ["node-1"] = ("not json", 0) };
        var monitor = NewMonitor(center, state, new Dictionary<string, int>(), new List<string>());
        await monitor.PollOnce();
        Assert.Empty(store.Query(source: "ssl").ToList());
    }

    [Fact]
    public void RenewWindow_Is30Days()
    {
        Assert.Equal(TimeSpan.FromDays(30), SslMonitor.RenewWindow);
    }
}
