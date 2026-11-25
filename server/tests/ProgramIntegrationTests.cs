using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TaoOps.Server.Audit;
using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using TaoOps.Server.Waf;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 集成测试共用夹具：TestServer 承载真实 Program 装配（端点/中间件/托管服务全接线）。
/// 环境变量在应用构建前置入（Program 顶层语句直读进程环境）。
/// </summary>
public sealed class TaoOpsWebAppFactory : WebApplicationFactory<Program>
{
    public const string AdminUser = "it-admin";
    public const string AdminPassword = "it-s3cret-9";

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "taoops-it-" + Guid.NewGuid().ToString("N"));

    public TaoOpsWebAppFactory()
    {
        Environment.SetEnvironmentVariable("TAOOPS_INSECURE", "1");
        Environment.SetEnvironmentVariable("TAOOPS_DATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("TAOOPS_INGEST_POLL_SECONDS", "3600");
        // 登录锁定阈值调低：集成测试用 2 次失败即锁（锁定路径 423 可达）。
        Environment.SetEnvironmentVariable("TAOOPS_AUTH_MAX_FAILURES", "2");
    }

    /// <summary>引导首个管理员（幂等：已存在即跳过）。</summary>
    public async Task BootstrapAdminAsync()
    {
        using var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/bootstrap",
            new { username = AdminUser, password = AdminPassword });
        Assert.True(res.IsSuccessStatusCode, $"bootstrap failed: {res.StatusCode}");
    }

    /// <summary>以管理员身份登录并返回 token；账号库为空时先引导再登录（幂等）。</summary>
    public async Task<string> LoginAdminAsync()
    {
        using var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login",
            new { username = AdminUser, password = AdminPassword });
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            // 引导首个管理员（仅账号库为空时放行；已有账号则 403，忽略后重试登录）。
            await client.PostAsJsonAsync("/api/auth/bootstrap",
                new { username = AdminUser, password = AdminPassword });
            res = await client.PostAsJsonAsync("/api/auth/login",
                new { username = AdminUser, password = AdminPassword });
        }
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);
    }
}

[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<TaoOpsWebAppFactory>;

/// <summary>端点接线集成测试：Program 顶层装配的 /api 面全量过一遍真实管道。</summary>
[Collection("Integration")]
public class ProgramEndpointTests
{
    private readonly TaoOpsWebAppFactory _factory;
    private readonly HttpClient _client;

    public ProgramEndpointTests(TaoOpsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AsAdmin()
    {
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.LoginAdminAsync());
    }

    [Fact]
    public async Task Root_InsecureBanner_Served()
    {
        var res = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("insecure", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_WithoutToken_Rejected401()
    {
        // 鉴权中间件：无 token 访问 /api → 401（匿名白名单之外）。
        var res = await _client.GetAsync("/api/waf/rules");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task WafRules_FullCrud_Lifecycle()
    {
        await AsAdmin();

        var create = new
        {
            id = "it-r1", name = "it-rule", category = "Sqli", action = "Deny",
            pattern = "union select", priority = 5, enabled = true,
        };
        var created = await _client.PostAsJsonAsync("/api/waf/rules", create);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var dup = await _client.PostAsJsonAsync("/api/waf/rules", create);
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);

        var list = await _client.GetAsync("/api/waf/rules");
        list.EnsureSuccessStatusCode();
        Assert.Contains("it-r1", await list.Content.ReadAsStringAsync());

        var updated = await _client.PutAsJsonAsync("/api/waf/rules/it-r1",
            new { id = "it-r1", name = "it-rule-v2", category = "Sqli", action = "Deny", pattern = "union", priority = 6, enabled = true });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var missing = await _client.PutAsJsonAsync("/api/waf/rules/ghost",
            new { id = "ghost", name = "n", category = "Sqli", action = "Deny", pattern = "p", priority = 1, enabled = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var disabled = await _client.PatchAsJsonAsync("/api/waf/rules/it-r1/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var patchMissing = await _client.PatchAsJsonAsync("/api/waf/rules/ghost/enabled", new { enabled = true });
        Assert.Equal(HttpStatusCode.NotFound, patchMissing.StatusCode);

        var deleted = await _client.DeleteAsync("/api/waf/rules/it-r1");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var deletedAgain = await _client.DeleteAsync("/api/waf/rules/it-r1");
        Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);
    }

    [Fact]
    public async Task WafSettings_GetPut_Roundtrip()
    {
        await AsAdmin();
        var get = await _client.GetAsync("/api/waf/settings");
        get.EnsureSuccessStatusCode();
        var before = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(before.GetProperty("enabled").GetBoolean());

        var put = await _client.PutAsJsonAsync("/api/waf/settings", new { enabled = true, denyStatusCode = 403 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    [Fact]
    public async Task WafImport_BadPayload_BadRequest()
    {
        await AsAdmin();
        var bad = await _client.PostAsync("/api/waf/rules/import", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task WafImport_Export_Roundtrip()
    {
        await AsAdmin();
        var export = await _client.GetAsync("/api/waf/rules/export");
        export.EnsureSuccessStatusCode();
        var json = await export.Content.ReadAsStringAsync();
        Assert.Contains("rules", json);

        var import = await _client.PostAsync("/api/waf/rules/import", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
    }

    [Fact]
    public async Task WafConfig_Preview_ContainsAccessLua()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/waf/config/preview");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("access_by_lua", body);
        Assert.Contains("normalize_for_match", body); // 归一化前置层随产物可见
    }

    [Fact]
    public async Task WafEvents_Query_EmptyOk()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/waf/events");
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SecEvents_Query_EmptyOk()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/sec/events");
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Auth_LoginBadCredentials_401()
    {
        var res = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = TaoOpsWebAppFactory.AdminUser, password = "totally-wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Auth_LoginMeLogout_FullSession()
    {
        var token = await _factory.LoginAdminAsync();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var me = await _client.GetAsync("/api/auth/me");
        me.EnsureSuccessStatusCode();
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TaoOpsWebAppFactory.AdminUser, body.GetProperty("user").GetString());
        Assert.Equal("Admin", body.GetProperty("role").GetString());

        var logout = await _client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        // 登出即作废：原 token 再访问 → 401。
        var after = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Auth_FailuresReachThreshold_Locked423()
    {
        // 专用账号走锁定路径，不污染其他用例的登录态（阈值 2，进程级旋钮）。
        await AsAdmin();
        var added = await _client.PostAsJsonAsync("/api/auth/users",
            new { username = "it-locked", password = "pw-123456", role = "Operator" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        for (var i = 0; i < 2; i++)
        {
            var bad = await _client.PostAsJsonAsync("/api/auth/login",
                new { username = "it-locked", password = "nope" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
        var locked = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "it-locked", password = "pw-123456" });
        Assert.Equal((HttpStatusCode)423, locked.StatusCode); // 账号锁定专用状态码
    }

    [Fact]
    public async Task Auth_Users_AdminOnly_OperatorForbidden()
    {
        await AsAdmin();
        var added = await _client.PostAsJsonAsync("/api/auth/users",
            new { username = "it-operator", password = "pw-123456", role = "Operator" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var dup = await _client.PostAsJsonAsync("/api/auth/users",
            new { username = "it-operator", password = "pw-123456", role = "Operator" });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);

        var list = await _client.GetAsync("/api/auth/users");
        list.EnsureSuccessStatusCode();
        Assert.Contains("it-operator", await list.Content.ReadAsStringAsync());

        // Operator 角色访问 Admin-only 前缀 → 403。
        var opLogin = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "it-operator", password = "pw-123456" });
        opLogin.EnsureSuccessStatusCode();
        var opToken = (await opLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new("Bearer", opToken);
        var forbidden = await _client.GetAsync("/api/auth/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // 回到 Admin 清理：删除用户 → 200；再删 → 404。
        await AsAdmin();
        var removed = await _client.DeleteAsync("/api/auth/users/it-operator");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var removedAgain = await _client.DeleteAsync("/api/auth/users/it-operator");
        Assert.Equal(HttpStatusCode.NotFound, removedAgain.StatusCode);
    }

    [Fact]
    public async Task Audit_QueryVerifyExport_Served()
    {
        await AsAdmin();
        var query = await _client.GetAsync("/api/audit");
        query.EnsureSuccessStatusCode();

        var verify = await _client.GetAsync("/api/audit/verify");
        verify.EnsureSuccessStatusCode();
        var body = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("valid").GetBoolean());

        var csv = await _client.GetAsync("/api/audit/export?format=csv");
        csv.EnsureSuccessStatusCode();
        Assert.Contains("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("seq,ts,user", await csv.Content.ReadAsStringAsync());

        var json = await _client.GetAsync("/api/audit/export");
        json.EnsureSuccessStatusCode();
        Assert.Contains("application/json", json.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task AuditMiddleware_WriteRequest_LeavesTrail()
    {
        await AsAdmin();
        var before = _factory.Services.GetRequiredService<AuditStore>().Query().Count;
        await _client.PostAsJsonAsync("/api/waf/rules",
            new { id = $"it-audit-{Guid.NewGuid():N}", name = "n", category = "Sqli", action = "Log", pattern = "p", priority = 1, enabled = true });

        var events = _factory.Services.GetRequiredService<AuditStore>().Query();
        Assert.True(events.Count > before, "write request must append audit entry");
        Assert.Contains(events, e => e.Action == "POST" && e.Target == "/api/waf/rules");
    }
}

/// <summary>WebSocket 桥接集成测试：终端/日志双向透传走真实 WS 管道。</summary>
[Collection("Integration")]
public class ProgramWebSocketTests
{
    private readonly TaoOpsWebAppFactory _factory;

    public ProgramWebSocketTests(TaoOpsWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Terminal_NonWebSocketRequest_400()
    {
        using var client = _factory.CreateClient();
        var res = await client.GetAsync("/ws/agent/any-node/terminal");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Logs_NonWebSocketRequest_400()
    {
        using var client = _factory.CreateClient();
        var res = await client.GetAsync("/ws/agent/any-node/logs?path=/tmp/x");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Terminal_UnknownNode_404()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            wsClient.ConnectAsync(new Uri("http://localhost/ws/agent/ghost-node/terminal"), CancellationToken.None));
    }

    [Fact]
    public async Task Terminal_RoundTrip_BothDirections()
    {
        var hub = _factory.Services.GetRequiredService<NodeStreamHub>();
        var session = hub.AttachTerm("it-ws-node", "it-ws-sess-1");
        var wsClient = _factory.Server.CreateWebSocketClient();
        using var ws = await wsClient.ConnectAsync(
            new Uri("http://localhost/ws/agent/it-ws-node/terminal?session=it-ws-sess-1"), CancellationToken.None);

        // 下行：WS 客户端 → 会话 Outbound（中控 → Agent 输入）。
        var payload = new byte[] { 1, 2, 3 };
        await ws.SendAsync(new ArraySegment<byte>(payload), System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
        var received = await session.Outbound.Reader.ReadAsync();
        Assert.Equal(payload, received.Data.ToArray());

        // 上行：会话 Inbound（Agent → 中控输出）→ WS 客户端。
        await session.Inbound.Writer.WriteAsync(new TaoOps.Server.Grpc.TermData
        {
            NodeId = "it-ws-node",
            SessionId = "it-ws-sess-1",
            Data = Google.Protobuf.ByteString.CopyFromUtf8("pong"),
        });
        var buf = new byte[64];
        var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
        Assert.Equal("pong", Encoding.UTF8.GetString(buf, 0, res.Count));

        // 关闭帧与服务端收尾存在竞态（服务端收到 Close 即拆链），关闭动作尽力而为。
        try
        {
            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (System.IO.IOException) { }
        catch (System.Net.WebSockets.WebSocketException) { }
    }

    [Fact]
    public async Task Logs_SubscribeBroadcastUnsubscribe_FullCycle()
    {
        var hub = _factory.Services.GetRequiredService<NodeStreamHub>();
        var logSession = hub.AttachLog("it-ws-logs");
        var wsClient = _factory.Server.CreateWebSocketClient();
        using var ws = await wsClient.ConnectAsync(
            new Uri("http://localhost/ws/agent/it-ws-logs/logs?path=/tmp/it.log&tail=5"), CancellationToken.None);

        // 连接即下发订阅控制（Agent 侧 Outbound 可见）。
        var sub = await logSession.Outbound.Reader.ReadAsync();
        Assert.Equal("subscribe", sub.Action);
        Assert.Equal("/tmp/it.log", sub.Path);
        Assert.Equal(5, sub.TailLines);

        // Agent 广播日志块 → WS 客户端收到。
        logSession.Broadcast(new TaoOps.Server.Grpc.LogChunk
        {
            NodeId = "it-ws-logs",
            Data = Google.Protobuf.ByteString.CopyFromUtf8("line-1\n"),
        });
        var buf = new byte[64];
        var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
        Assert.Equal("line-1\n", Encoding.UTF8.GetString(buf, 0, res.Count));

        // 控制台断开 → 退订控制下发（防 Agent 侧 tail 协程泄漏）。
        // 关闭帧与服务端收尾存在竞态（服务端收到 Close 即拆链），关闭动作尽力而为。
        try
        {
            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (System.IO.IOException) { }
        catch (System.Net.WebSockets.WebSocketException) { }
        var unsub = await logSession.Outbound.Reader.ReadAsync();
        Assert.Equal("unsubscribe", unsub.Action);
        Assert.Equal("/tmp/it.log", unsub.Path);
    }
}

/// <summary>传输装配三分支：TLS / h2c / legacy --urls 兼容路径。</summary>
[Collection("Integration")]
public class TransportConfigTests
{
    [Fact]
    public async Task TlsMode_BindsHttps_AndWritesPfx()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taoops-tls-" + Guid.NewGuid().ToString("N"));
        var port = 5521 + Random.Shared.Next(100);
        Environment.SetEnvironmentVariable("TAOOPS_DATA_DIR", dir);
        Environment.SetEnvironmentVariable("TAOOPS_PORT", port.ToString());
        try
        {
            var builder = WebApplication.CreateBuilder();
            Program.ConfigureTransport(builder, insecure: false, legacyUrls: false);
            using var app = builder.Build();
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            Assert.Contains(addresses, a => a.StartsWith("https://") && a.Contains(port.ToString()));
            await app.StopAsync();
            Assert.True(File.Exists(Path.Combine(dir, "server.pfx")), "TLS 模式必须落自签证书");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task InsecureMode_BindsHttp2Clear()
    {
        var port = 5521 + Random.Shared.Next(100);
        Environment.SetEnvironmentVariable("TAOOPS_PORT", port.ToString());
        var builder = WebApplication.CreateBuilder();
        Program.ConfigureTransport(builder, insecure: true, legacyUrls: false);
        using var app = builder.Build();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.Contains(addresses, a => a.StartsWith("http://") && a.Contains(port.ToString()));
        await app.StopAsync();
    }

    [Fact]
    public void InsecureWithLegacyUrls_DoesNotTouchKestrel()
    {
        // M1 E2E 兼容路径：不配置 Kestrel 监听，沿用 --urls。
        var builder = WebApplication.CreateBuilder();
        Program.ConfigureTransport(builder, insecure: true, legacyUrls: true);
        using var app = builder.Build(); // 不抛即通过；监听交给 --urls
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.Empty(addresses);
    }
}
