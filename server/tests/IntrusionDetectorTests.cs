using System.IO;
using TaoOps.Server.Audit;
using TaoOps.Server.Sec;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 防入侵检测器实效断言（A8-A11）：每条用例至少一条可失败断言，
/// 告警以 AuditStore 哈希链为真源，VerifyChain 验篡改可检出。
/// </summary>
public class IntrusionDetectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"taoops-sec-{Guid.NewGuid():N}");
    private readonly AuditStore _audit;
    private readonly IntrusionDetector _det;

    public IntrusionDetectorTests()
    {
        Directory.CreateDirectory(_dir);
        _audit = new AuditStore(_dir);
        _det = new IntrusionDetector(_audit);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- A8 异常登录时段 ----

    [Fact]
    public void A8_LoginOutsideQuietHours_EmitsLoginHourAlert()
    {
        // 静默 23-06：14 点登录在窗口外 → 告警。
        var alert = _det.OnLoginSuccess("alice", "1.2.3.4", Ts(14), hourOverride: 14);
        Assert.Equal(SecAlertTypes.LoginHour, alert.Type);
        Assert.Equal("medium", alert.Severity);
        Assert.Equal("alice", alert.User);
        Assert.Single(_det.Query(type: SecAlertTypes.LoginHour));
    }

    [Fact]
    public void A8_LoginInsideQuietHours_NoAlert()
    {
        // 23 点与 02 点都在静默窗口内 → 不告警（实效断言：Query 为空）。
        _det.OnLoginSuccess("alice", "1.2.3.4", Ts(23), hourOverride: 23);
        _det.OnLoginSuccess("bob", "1.2.3.4", Ts(2), hourOverride: 2);
        Assert.Empty(_det.Query(type: SecAlertTypes.LoginHour));
    }

    [Fact]
    public void A8_QuietHours_CrossMidnightWindow()
    {
        // 跨零点窗口 23-06：22 点在窗口外（异常），23 点在窗口内（正常）。
        _det.OnLoginSuccess("alice", "ip", Ts(22), hourOverride: 22);
        Assert.Single(_det.Query(type: SecAlertTypes.LoginHour));
        _det.OnLoginSuccess("bob", "ip", Ts(23), hourOverride: 23);
        Assert.Single(_det.Query(type: SecAlertTypes.LoginHour));
    }

    [Fact]
    public void A8_QuietHours_InvalidEnvFallsBackToDefault()
    {
        Assert.Equal((23, 6), IntrusionDetector.ParseQuietHours("garbage"));
        Assert.Equal((1, 5), IntrusionDetector.ParseQuietHours("1-5"));
    }

    // ---- A9 非常规命令 ----

    [Fact]
    public void A9_FirstTimeAction_EmitsUnusualAlert_RepeatDoesNot()
    {
        var a1 = _det.OnUserAction("alice", "db.drop", "ip", Ts(10));
        Assert.Equal(SecAlertTypes.UnusualAction, a1.Type);
        Assert.Equal("low", a1.Severity);
        // 同用户重复同 action：不告警。
        var a2 = _det.OnUserAction("alice", "db.drop", "ip", Ts(11));
        Assert.StartsWith("(no alert)", a2.Detail);
        Assert.Single(_det.Query(type: SecAlertTypes.UnusualAction));
    }

    [Fact]
    public void A9_DifferentUsers_TrackedIndependently()
    {
        _det.OnUserAction("alice", "db.drop", "ip", Ts(10));
        // bob 首次执行同 action：对 bob 仍是首次 → 告警。
        var b1 = _det.OnUserAction("bob", "db.drop", "ip", Ts(11));
        Assert.Equal(SecAlertTypes.UnusualAction, b1.Type);
        Assert.Equal(2, _det.Query(type: SecAlertTypes.UnusualAction).Count);
    }

    // ---- A10 暴力尝试聚合 ----

    [Fact]
    public void A10_ThresholdFailures_EmitSingleAggAlert_NoRepeatInWindow()
    {
        // 默认阈值 3：前两次不告警，第三次聚合告警，窗口内第四次不再重复。
        _det.OnSecFailure("9.9.9.9", Ts(10));
        _det.OnSecFailure("9.9.9.9", Ts(11));
        var third = _det.OnSecFailure("9.9.9.9", Ts(12));
        Assert.Equal(SecAlertTypes.BruteforceAgg, third.Type);
        Assert.Equal("high", third.Severity);
        _det.OnSecFailure("9.9.9.9", Ts(13));
        Assert.Single(_det.Query(type: SecAlertTypes.BruteforceAgg));
    }

    [Fact]
    public void A10_DifferentIps_IndependentWindows()
    {
        _det.OnSecFailure("1.1.1.1", Ts(10));
        _det.OnSecFailure("1.1.1.1", Ts(11));
        _det.OnSecFailure("2.2.2.2", Ts(12)); // 另一 IP 计 1 次
        Assert.Empty(_det.Query(type: SecAlertTypes.BruteforceAgg));
    }

    [Fact]
    public void A10_OldFailuresOutsideWindow_DoNotCount()
    {
        long window = _det.AggWindowMs;
        // 两条在窗口外（间隔超过窗口），第三条只与自身同窗 → 不达阈值。
        _det.OnSecFailure("3.3.3.3", Ts(10));
        _det.OnSecFailure("3.3.3.3", Ts(10) + window + 1);
        var third = _det.OnSecFailure("3.3.3.3", Ts(10) + window + 2);
        Assert.StartsWith("(no alert)", third.Detail);
        Assert.Empty(_det.Query(type: SecAlertTypes.BruteforceAgg));
    }

    // ---- A11 查询与审计链可溯 ----

    [Fact]
    public void A11_Query_FiltersByTypeSeverityAndTime()
    {
        _det.OnLoginSuccess("alice", "ip", Ts(14), hourOverride: 14);   // login_hour/medium
        _det.OnUserAction("alice", "db.drop", "ip", Ts(15));             // unusual/low
        _det.OnSecFailure("9.9.9.9", Ts(16));
        _det.OnSecFailure("9.9.9.9", Ts(17));
        _det.OnSecFailure("9.9.9.9", Ts(18));                            // bruteforce/high

        Assert.Single(_det.Query(type: SecAlertTypes.LoginHour));
        Assert.Single(_det.Query(severity: "high"));
        // 时间过滤：只取 16ms 之后的 → login_hour(14) 排除。
        var from16 = _det.Query(fromMs: Ts(16));
        Assert.DoesNotContain(from16, a => a.Type == SecAlertTypes.LoginHour);
        // 倒序：最新在前。
        var all = _det.Query();
        Assert.True(all[0].Ts >= all[^1].Ts);
    }

    [Fact]
    public void A11_EveryAlert_InAuditHashChain_TamperDetectable()
    {
        _det.OnLoginSuccess("alice", "ip", Ts(14), hourOverride: 14);
        _det.OnUserAction("alice", "db.drop", "ip", Ts(15));
        _det.OnSecFailure("9.9.9.9", Ts(16));
        _det.OnSecFailure("9.9.9.9", Ts(17));
        _det.OnSecFailure("9.9.9.9", Ts(18));

        // 三条告警都进了审计链且链完整。
        var alerts = _det.Query();
        Assert.Equal(3, alerts.Count);
        Assert.All(alerts, a => Assert.True(a.AuditSeq > 0));
        var (valid, broken) = _audit.VerifyChain();
        Assert.True(valid);
        Assert.Null(broken);

        // 篡改历史条目 → 断链可检出（实效断言：VerifyChain 返回 false）。
        var lines = File.ReadAllLines(_audit.FilePath);
        var tampered = lines[0].Replace("\"Action\":\"sec.alert\"", "\"Action\":\"sec.alert.TAMPERED\"");
        Assert.NotEqual(lines[0], tampered);
        File.WriteAllLines(_audit.FilePath, new[] { tampered }.Concat(lines.Skip(1)));
        _audit.Reload();
        var (stillValid, brokenAt) = _audit.VerifyChain();
        Assert.False(stillValid);
        Assert.NotNull(brokenAt);
    }

    private static long Ts(int minute) => new DateTimeOffset(2026, 11, 23, 12, minute % 60, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();
}
