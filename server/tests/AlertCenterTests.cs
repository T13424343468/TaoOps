using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TaoOps.Server.Alerts;
using TaoOps.Server.Audit;
using TaoOps.Server.Sec;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>告警账本单元测试（FR-A19）：落盘/重载/过滤/已读/保留策略。动进程级环境变量，并入 Integration 集合串行。</summary>
[Collection("Integration")]
public sealed class AlertStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taoops-alerts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Append_PersistsToJsonl_AndReloads()
    {
        // 用近期时间戳：保留策略（默认 90 天）会清掉过期条目，历史固定时间戳不可用。
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore();
        var first = store.Append("disk", "critical", "node-1", "disk usage 91% >= 90%", ts);
        Assert.False(string.IsNullOrEmpty(first.Id));

        // 新实例重载同一数据目录：账本不丢。
        var reloaded = new AlertStore(_dir);
        var all = reloaded.Query();
        Assert.Single(all);
        Assert.Equal("disk", all[0].Source);
        Assert.Equal("critical", all[0].Level);
        Assert.Equal("node-1", all[0].NodeId);
        Assert.Equal(ts, all[0].Ts);
        Assert.False(all[0].Read);
    }

    [Fact]
    public void Query_FiltersByLevelSourceNodeTimeUnread()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore();
        store.Append("disk", "critical", "node-1", "d1", now);
        store.Append("intrusion", "warn", "node-1", "d2", now + 1_000);
        store.Append("intrusion", "critical", "node-2", "d3", now + 2_000);
        store.Append("backup", "warn", "node-2", "d4", now + 3_000);

        Assert.Equal(4, store.Query().Count);
        Assert.Equal(2, store.Query(level: "critical").Count);
        Assert.Equal(2, store.Query(source: "intrusion").Count);
        Assert.Equal(2, store.Query(nodeId: "node-1").Count);
        Assert.Equal(2, store.Query(fromMs: now + 1_000, toMs: now + 2_000).Count);
        // 时间倒序。
        Assert.Equal(now + 3_000, store.Query()[0].Ts);

        store.MarkRead(store.Query(source: "disk")[0].Id);
        Assert.Equal(3, store.Query(unreadOnly: true).Count);
        Assert.Single(store.Query(level: "critical", unreadOnly: true));
    }

    [Fact]
    public void MarkRead_UnknownId_ReturnsFalse()
    {
        var store = CreateStore();
        Assert.False(store.MarkRead("no-such-id"));
    }

    [Fact]
    public void MarkRead_PersistsAcrossReload()
    {
        string id;
        var store = CreateStore();
        id = store.Append("disk", "warn", "n", "d").Id;
        Assert.True(store.MarkRead(id));

        var reloaded = new AlertStore(_dir);
        var alert = Assert.Single(reloaded.Query());
        Assert.True(alert.Read);
        Assert.Equal(0, reloaded.UnreadCount());
    }

    [Fact]
    public void MarkAllRead_MarksEverything_AndCounts()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore();
        store.Append("disk", "warn", "n", "d1", now);
        store.Append("disk", "warn", "n", "d2", now + 1);
        store.Append("cert", "info", "n", "d3", now + 2);
        Assert.Equal(3, store.UnreadCount());
        Assert.Equal(3, store.MarkAllRead());
        Assert.Equal(0, store.UnreadCount());
        Assert.Equal(0, store.MarkAllRead()); // 重复标记计 0。
    }

    [Fact]
    public void Retention_PrunesExpired_OnAppendAndLoad()
    {
        Environment.SetEnvironmentVariable("TAOOPS_ALERTS_RETENTION_DAYS", "1");
        try
        {
            var store = CreateStore();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 先入过期条目，再入新鲜条目：追加时按当前时钟清理过期。
            store.Append("disk", "warn", "n", "stale", now - 2 * 24 * 60 * 60 * 1000L);
            store.Append("disk", "warn", "n", "fresh", now);
            Assert.Single(store.Query());
            Assert.Equal("fresh", store.Query()[0].Detail);

            var reloaded = new AlertStore(_dir);
            Assert.Single(reloaded.Query()); // 重载同样只剩未过期条目。
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAOOPS_ALERTS_RETENTION_DAYS", null);
        }
    }

    [Fact]
    public void MalformedLines_AreSkipped_OnLoad()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "alerts.jsonl"), "{not-json}\n");
        var store = new AlertStore(_dir);
        Assert.Empty(store.Query());
    }

    private AlertStore CreateStore() => new(_dir);
}

/// <summary>告警中心接线测试（FR-A19）：磁盘水位边缘触发+迟滞、入侵检测转发映射。</summary>
public sealed class AlertCenterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taoops-alertctr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void DiskWatermark_EdgeTriggersOnce_ThenResetsBelowHysteresis()
    {
        var center = new AlertCenter(new AlertStore(_dir));
        // 91%：边缘触发 critical。
        var alert = center.CheckDiskWatermark("n1", 91);
        Assert.NotNull(alert);
        Assert.Equal("critical", alert!.Level);
        Assert.Equal("disk", alert.Source);
        // 持续高位：不重复告警。
        Assert.Null(center.CheckDiskWatermark("n1", 95));
        // 回落到 80%（<85% 迟滞复位）。
        Assert.Null(center.CheckDiskWatermark("n1", 80));
        // 再次冲高：重新触发。
        Assert.NotNull(center.CheckDiskWatermark("n1", 90));
        // 88%（≥85 迟滞区间内）：不触发也不复位。
        Assert.Null(center.CheckDiskWatermark("n1", 88));
        // 独立节点互不影响。
        Assert.NotNull(center.CheckDiskWatermark("n2", 99));
    }

    [Fact]
    public void IntrusionForwarding_MapsSeverityToUnifiedLevel()
    {
        // 近期时间戳（保留策略会清掉过期条目），按 Ts 排序断言映射。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var center = new AlertCenter(new AlertStore(_dir));
        center.IngestIntrusion(new SecAlert { Type = "bruteforce_agg", Severity = "high", Detail = "x", Ts = now });
        center.IngestIntrusion(new SecAlert { Type = "login_hour", Severity = "medium", Detail = "y", Ts = now + 1 });
        center.IngestIntrusion(new SecAlert { Type = "unusual_action", Severity = "low", Detail = "z", Ts = now + 2 });

        var all = new AlertStore(_dir).Query();
        Assert.Equal(3, all.Count);
        Assert.Equal("critical", all.First(a => a.Detail.EndsWith("x")).Level);
        Assert.Equal("warn", all.First(a => a.Detail.EndsWith("y")).Level);
        Assert.Equal("info", all.First(a => a.Detail.EndsWith("z")).Level);
        Assert.All(all, a => Assert.Equal("intrusion", a.Source));
    }

    [Fact]
    public void IntrusionDetector_ForwardsToAlertCenter()
    {
        var center = new AlertCenter(new AlertStore(_dir));
        var detector = new IntrusionDetector(new AuditStore(_dir), center);
        // 静默时段外（默认 23-06，取正午）→ 触发 login_hour 告警并转发。
        var ts = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        detector.OnLoginSuccess("alice", "1.2.3.4", ts, hourOverride: 12);

        var unified = new AlertStore(_dir).Query();
        Assert.Single(unified);
        Assert.Equal("intrusion", unified[0].Source);
        Assert.Equal("warn", unified[0].Level);
        Assert.Contains("login_hour", unified[0].Detail);
    }

    [Fact]
    public void IntrusionDetector_WithoutCenter_StillWorks()
    {
        // 可选依赖缺省：不转发也不崩（既有测试口径不破坏）。
        var detector = new IntrusionDetector(new AuditStore(_dir));
        var ts = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var alert = detector.OnLoginSuccess("bob", "1.2.3.4", ts, hourOverride: 12);
        Assert.Equal("login_hour", alert.Type);
        Assert.Empty(new AlertStore(_dir).Query());
    }
}

/// <summary>告警中心端点集成测试（FR-A19）：真实管道过鉴权与读写。</summary>
[Collection("Integration")]
public class AlertsEndpointTests
{
    private readonly TaoOpsWebAppFactory _factory;

    public AlertsEndpointTests(TaoOpsWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Alerts_RequireAuth()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/api/alerts");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Alerts_ListUnreadAndMarkRead()
    {
        var token = await _factory.LoginAdminAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // 造一条告警：直接经 DI 取 AlertCenter 落账（来源接线已有单测覆盖）。
        var center = _factory.Services.GetRequiredService<AlertCenter>();
        var alert = center.Raise("backup", "warn", "it-node", "backup failed: verify mismatch");

        // 列表与未读数。
        var list = await client.GetFromJsonAsync<JsonElement>("/api/alerts?source=backup");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("backup failed: verify mismatch", list[0].GetProperty("detail").GetString());
        var unread = await client.GetFromJsonAsync<JsonElement>("/api/alerts/unread");
        Assert.True(unread.GetProperty("count").GetInt32() >= 1);

        // 标记已读 → 未读数回落；未知 Id 404。
        var mark = await client.PostAsync($"/api/alerts/{alert.Id}/read", content: null);
        Assert.Equal(HttpStatusCode.OK, mark.StatusCode);
        var markUnknown = await client.PostAsync("/api/alerts/no-such-id/read", content: null);
        Assert.Equal(HttpStatusCode.NotFound, markUnknown.StatusCode);

        // 过滤参数：unreadOnly=true 不再包含已读条目。
        var unreadList = await client.GetFromJsonAsync<JsonElement>("/api/alerts?source=backup&unread=true");
        Assert.Equal(0, unreadList.GetArrayLength());
    }

    [Fact]
    public async Task Alerts_ReadAll_MarksEverything()
    {
        var token = await _factory.LoginAdminAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var center = _factory.Services.GetRequiredService<AlertCenter>();
        center.Raise("cert", "info", "it-node", "cert expires in 21 days");

        var res = await client.PostAsJsonAsync("/api/alerts/read-all", new { });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("marked").GetInt32() >= 1);
        var unread = await client.GetFromJsonAsync<JsonElement>("/api/alerts/unread");
        Assert.Equal(0, unread.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Alerts_WriteBlockedForReadOnly()
    {
        var token = await _factory.LoginAdminAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        // 造一个只读用户（Admin 建号）。
        await client.PostAsJsonAsync("/api/auth/users", new { username = "ro-alerts", password = "ro-pass-1", role = "ReadOnly" });

        var roClient = _factory.CreateClient();
        var login = await roClient.PostAsJsonAsync("/api/auth/login", new { username = "ro-alerts", password = "ro-pass-1" });
        login.EnsureSuccessStatusCode();
        var roToken = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        roClient.DefaultRequestHeaders.Authorization = new("Bearer", roToken);

        var res = await roClient.PostAsync("/api/alerts/read-all", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
