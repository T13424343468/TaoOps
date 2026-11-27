using System.Net;
using System.Text.Json;
using TaoOps.Server.Auth;
using TaoOps.Server.Push;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次四第一波：SsrfGuard 边缘分支（白名单 IP/映射形态/非法 CIDR 条目、
/// DNS 空记录、InCidr 族不匹配与坏前缀）、推送三库落盘分支（损坏文件兜底、
/// 保留期清理、查询过滤组合）、TotpStore 文件态分支（缺失/删除/用户名净化）。
/// </summary>
public class Batch4BackfillTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"taoops-b4-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    // ---------- SsrfGuard ----------

    [Fact]
    public async Task ValidateTarget_DnsEmptyRecords_Rejected()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://empty.example/hook", [],
            _ => Task.FromResult(Array.Empty<IPAddress>()));
        Assert.NotNull(error);
        Assert.Contains("no addresses", error);
    }

    [Fact]
    public async Task ValidateTarget_WhitelistHostname_CaseInsensitive_AndSkipsHostnameEntriesForResolvedIps()
    {
        // 主机名条目大小写不敏感命中即放行，不触达 DNS。
        var dnsCalled = false;
        var ok = await SsrfGuard.ValidateTargetAsync("http://INTERNAL.Example/hook", ["internal.EXAMPLE"],
            _ => { dnsCalled = true; return Task.FromResult(Array.Empty<IPAddress>()); });
        Assert.Null(ok);
        Assert.False(dnsCalled);

        // 主机名条目不匹配主机时，进入解析记录循环后被当作非 CIDR 条目跳过，不影响判定。
        var error = await SsrfGuard.ValidateTargetAsync("http://evil.example/hook", ["other.example"],
            _ => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateTarget_WhitelistIpLiteralEntry_MatchesResolvedAddress()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://host.example/hook", ["93.184.216.34"],
            _ => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateTarget_WhitelistMappedV4Entry_MatchesPlainV4Address()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://host.example/hook", ["::ffff:10.0.0.7"],
            _ => Task.FromResult(new[] { IPAddress.Parse("10.0.0.7") }));
        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateTarget_InvalidCidrEntry_IgnoredNotFatal()
    {
        // 非法 CIDR 条目（前缀越界）在守卫层忽略，不抛出、不放行该条目之外的目标。
        var error = await SsrfGuard.ValidateTargetAsync("http://10.9.9.9/hook", ["10.0.0.0/99"], null);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("::ffff:10.0.0.1", true)]   // v4 映射形态映射回 v4 后按内网表判定
    [InlineData("::ffff:93.184.216.34", false)]
    [InlineData("::", true)]                // 未指定地址
    public void IsPrivateIp_MappedAndUnspecified(string ip, bool expected)
    {
        Assert.Equal(expected, SsrfGuard.IsPrivateIp(IPAddress.Parse(ip)));
    }

    [Fact]
    public void InCidr_FamilyMismatchAndMappedRecursion()
    {
        // v6 目标对 v4 网段：直接不命中。
        Assert.False(SsrfGuard.InCidr(IPAddress.Parse("fc00::1"), "10.0.0.0/8"));
        // v4 映射形态对 v4 网段：映射后递归判定。
        Assert.True(SsrfGuard.InCidr(IPAddress.Parse("::ffff:10.1.2.3"), "10.0.0.0/8"));
        Assert.False(SsrfGuard.InCidr(IPAddress.Parse("::ffff:11.1.2.3"), "10.0.0.0/8"));
        // 前缀为负：格式非法。
        Assert.Throws<FormatException>(() => SsrfGuard.InCidr(IPAddress.Parse("1.2.3.4"), "1.2.3.0/-1"));
        // 缺少 '/' 或前缀非数字：格式非法。
        Assert.Throws<FormatException>(() => SsrfGuard.InCidr(IPAddress.Parse("1.2.3.4"), "1.2.3.0"));
        Assert.Throws<FormatException>(() => SsrfGuard.InCidr(IPAddress.Parse("1.2.3.4"), "1.2.3.0/x"));
    }

    // ---------- PushChannelStore ----------

    [Fact]
    public void ChannelStore_CorruptFile_FallsBackToEmpty()
    {
        var dir = Path.Combine(_dataDir, "push");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "channels.json"), "{not-json");
        var store = new PushChannelStore(_dataDir);
        Assert.Empty(store.List());
        // 兜底后可正常写入。
        store.Upsert(new PushChannel { Name = "a", Url = "http://a.example" });
        Assert.Single(store.List());
    }

    [Fact]
    public void ChannelStore_DeleteMissing_ReturnsFalse()
    {
        var store = new PushChannelStore(_dataDir);
        Assert.False(store.Delete("nope"));
    }

    // ---------- SsrfWhitelistStore ----------

    [Fact]
    public void WhitelistStore_CorruptFile_FallsBackToEmpty()
    {
        var dir = Path.Combine(_dataDir, "push");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ssrf-whitelist.json"), "[oops");
        var store = new SsrfWhitelistStore(_dataDir);
        Assert.Empty(store.List());
        store.Replace(["10.0.0.0/8"]);
        Assert.Single(store.List());
    }

    // ---------- PushLogStore ----------

    private PushLogStore MakeStore() => new(_dataDir);

    private static PushLogEntry Entry(long ts, string channelId = "c1", int? status = 200, bool ok = true) =>
        new() { Ts = ts, ChannelId = channelId, Url = "http://hook.example/x", StatusCode = status, Ok = ok, Attempt = 1 };

    [Fact]
    public void LogStore_CorruptLines_SkippedOnLoad()
    {
        var dir = Path.Combine(_dataDir, "push");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "log.jsonl");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllLines(path,
        [
            JsonSerializer.Serialize(Entry(now)),
            "{broken",
            JsonSerializer.Serialize(Entry(now + 1, "c2")),
        ]);
        var store = new PushLogStore(_dataDir);
        Assert.Equal(2, store.Query().Count);
    }

    [Fact]
    public void LogStore_LoadPrunesEntriesOlderThanRetention()
    {
        var dir = Path.Combine(_dataDir, "push");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "log.jsonl");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stale = now - 91L * 24 * 60 * 60 * 1000;
        File.WriteAllLines(path,
        [
            JsonSerializer.Serialize(Entry(stale)),
            JsonSerializer.Serialize(Entry(now)),
        ]);
        var store = new PushLogStore(_dataDir);
        var entries = store.Query();
        Assert.Single(entries);
        Assert.Equal(now, entries[0].Ts);
        // 过期行已从盘上重写移除。
        Assert.Single(File.ReadAllLines(path));
    }

    [Fact]
    public void LogStore_RetentionEnvOverride_ValidAndInvalid()
    {
        var original = Environment.GetEnvironmentVariable("TAOOPS_ALERTS_RETENTION_DAYS");
        try
        {
            Environment.SetEnvironmentVariable("TAOOPS_ALERTS_RETENTION_DAYS", "not-a-number");
            MakeStore(); // 解析失败走默认 90 天
            Environment.SetEnvironmentVariable("TAOOPS_ALERTS_RETENTION_DAYS", "0");
            MakeStore(); // 非正数同样走默认
            Environment.SetEnvironmentVariable("TAOOPS_ALERTS_RETENTION_DAYS", "1");
            MakeStore(); // 生效：1 天保留期
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_ALERTS_RETENTION_DAYS", original);
        }
    }

    [Fact]
    public void LogStore_AppendPrunesExpired_AndQueryFilters()
    {
        var store = MakeStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stale = now - 91L * 24 * 60 * 60 * 1000;
        store.Append(Entry(stale, "old"));      // 落库即过期
        store.Append(Entry(now, "c1"));
        store.Append(Entry(now + 1, "c1", status: 500, ok: false));
        store.Append(Entry(now + 2, "c2"));

        // Append 时过期条目被清理并重写盘。
        Assert.DoesNotContain(store.Query(), e => e.ChannelId == "old");

        // 全量：按时间倒序。
        var all = store.Query();
        Assert.Equal(3, all.Count);
        Assert.True(all[0].Ts >= all[1].Ts);

        // channelId 过滤。
        Assert.Equal(2, store.Query(channelId: "c1").Count);
        // from/to 组合过滤。
        Assert.Single(store.Query(fromMs: now + 2));
        Assert.Single(store.Query(toMs: now));
        Assert.Equal(2, store.Query(fromMs: now, toMs: now + 1).Count);
        // 组合：channel + from + to。
        Assert.Single(store.Query(channelId: "c1", fromMs: now + 1, toMs: now + 1));
    }

    // ---------- TotpStore ----------

    [Fact]
    public void TotpStore_LoadMissing_DeleteMissing_RoundTripAndSanitize()
    {
        var store = new TotpStore(_dataDir);
        Assert.Null(store.Load("ghost"));
        Assert.False(store.Delete("ghost"));

        // 用户名净化：仅保留字母数字与 _-.，其余剔除后落盘可回读。
        var account = new TotpAccount { Secret = "ABC234", Enabled = true, CreatedMs = 42, LastUsedStep = 7 };
        store.Save("a<b>c d.e-f_g", account);
        Assert.NotNull(store.Load("a<b>c d.e-f_g"));
        Assert.True(store.Delete("a<b>c d.e-f_g"));
        Assert.False(store.Delete("a<b>c d.e-f_g"));

        // 纯特殊字符用户名净化后为空文件名场景仍可保存（文件名 ".json"）。
        store.Save("###", new TotpAccount { Secret = "X" });
        Assert.NotNull(store.Load("###"));
    }
}
