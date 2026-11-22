using System.Text.Json;
using TaoOps.Server.Audit;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/breakpoint-m3-security.md A3（审计哈希链）。</summary>
public class AuditChainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taoops-audit-" + Guid.NewGuid().ToString("N"));

    public AuditChainTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AuditEvent Ev(string user, string action, long ts) => new()
    { Ts = ts, User = user, Role = "Admin", Action = action, Target = "/api/x", Ip = "10.0.0.1", Result = "200" };

    [Fact]
    public void Append_BuildsChain_VerifyTrue()
    {
        var store = new AuditStore(_dir);
        store.Append(Ev("admin", "POST", 1));
        store.Append(Ev("admin", "PUT", 2));
        store.Append(Ev("op", "DELETE", 3));
        var (valid, broken) = store.VerifyChain();
        Assert.True(valid);
        Assert.Null(broken);
    }

    [Fact]
    public void TamperedHistoryEntry_DetectedAfterReload()
    {
        // A3：篡改任一历史条目，重载后校验可检出。
        var store = new AuditStore(_dir);
        store.Append(Ev("admin", "POST", 1));
        store.Append(Ev("admin", "PUT", 2));
        store.Append(Ev("admin", "DELETE", 3));

        var lines = File.ReadAllLines(Path.Combine(_dir, "audit", "audit.jsonl"));
        // 改历史第一条的 User（模拟篡改者抹痕）。
        var modified = System.Text.RegularExpressions.Regex.Replace(
            lines[0], "\"User\":\"[^\"]*\"", "\"User\":\"ghost\"");
        Assert.NotEqual(lines[0], modified);
        File.WriteAllLines(Path.Combine(_dir, "audit", "audit.jsonl"), [modified, lines[1], lines[2]]);

        var reloaded = new AuditStore(_dir);
        var (valid, brokenAt) = reloaded.VerifyChain();
        Assert.False(valid);
        Assert.Equal(1, brokenAt);
    }

    [Fact]
    public void Query_FiltersByUserAndTimeRange()
    {
        var store = new AuditStore(_dir);
        store.Append(Ev("admin", "POST", 100));
        store.Append(Ev("op", "POST", 200));
        store.Append(Ev("admin", "PUT", 300));
        Assert.Equal(2, store.Query("admin").Count);
        Assert.Equal(2, store.Query(fromMs: 150, toMs: 350).Count);
        Assert.Single(store.Query("op", fromMs: 150));
        // 最新在前。
        Assert.Equal(300, store.Query()[0].Ts);
    }

    [Fact]
    public void Export_JsonAndCsv_WellFormed()
    {
        var store = new AuditStore(_dir);
        store.Append(Ev("ad,min", "POST", 1)); // 含逗号，CSV 需转义
        var json = store.ExportJson();
        Assert.Contains("\"User\":\"ad,min\"", json);
        var csv = store.ExportCsv();
        Assert.StartsWith("seq,ts,user,role,action,target,ip,result\n", csv);
        Assert.Contains("\"ad,min\"", csv);
    }
}
