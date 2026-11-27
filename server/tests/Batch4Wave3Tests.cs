using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using TaoOps.Server.Services;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次四第三波：告警阈值设置全矩阵、推送渠道空字段/删除不存在/白名单增删审计、
/// 外接建议配置角色闸与校验、K8s 建集群缺参与缺失集群操作 404、TOTP 端点空字段协变臂、
/// 改密旧密码不匹配审计。全部走真实端点管道，不需要外部依赖（缺失集群在客户端构造前即抛）。
/// </summary>
[Collection("Integration")]
public class Batch4Wave3Tests
{
    private readonly TaoOpsWebAppFactory _factory;
    private readonly HttpClient _client;

    public Batch4Wave3Tests(TaoOpsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AsAdmin() =>
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.LoginAdminAsync());

    private async Task<string> CreateUserWithPassword(string role, string username, string password)
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/auth/users", new { username, password, role });
        Assert.True(res.IsSuccessStatusCode, $"create user failed: {res.StatusCode}");
        return username;
    }

    private async Task<string> LoginAs(string username, string password)
    {
        using var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username, password });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static string Rnd(string prefix) => $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant()[..24];

    // ---- 告警阈值设置：PUT 全矩阵 + GET 回显 + 审计 ----

    [Fact]
    public async Task Settings_Alerts_RoleAndValidationMatrix()
    {
        // Operator → 403。
        var opUser = Rnd("setop");
        var opPass = "p4ssw0rd-setop";
        await CreateUserWithPassword("Operator", opUser, opPass);
        using var opClient = _factory.CreateClient();
        opClient.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(opUser, opPass));
        var forbidden = await opClient.PutAsJsonAsync("/api/settings/alerts",
            new { diskCriticalPct = 85, diskResetPct = 70 });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        await AsAdmin();

        // 触发阈值越界（0 / 100）→ 400。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync("/api/settings/alerts", new { diskCriticalPct = 0, diskResetPct = 70 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync("/api/settings/alerts", new { diskCriticalPct = 100, diskResetPct = 70 })).StatusCode);

        // 复位阈值越界（0 / 100）→ 400。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync("/api/settings/alerts", new { diskCriticalPct = 85, diskResetPct = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync("/api/settings/alerts", new { diskCriticalPct = 85, diskResetPct = 100 })).StatusCode);

        // 复位 ≥ 触发（迟滞口径）→ 400。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync("/api/settings/alerts", new { diskCriticalPct = 80, diskResetPct = 80 })).StatusCode);

        // 合法值 → 200，GET 回显一致，审计留痕。
        var ok = await _client.PutAsJsonAsync("/api/settings/alerts", new { diskCriticalPct = 85, diskResetPct = 70 });
        Assert.True(ok.IsSuccessStatusCode, $"valid put failed: {ok.StatusCode} {await ok.Content.ReadAsStringAsync()}");
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(85, body.GetProperty("disk_critical_pct").GetInt32());
        Assert.Equal(70, body.GetProperty("disk_reset_pct").GetInt32());

        var echo = await _client.GetFromJsonAsync<JsonElement>("/api/settings/alerts");
        Assert.Equal(85, echo.GetProperty("disk_critical_pct").GetInt32());
        Assert.Equal(70, echo.GetProperty("disk_reset_pct").GetInt32());

        var audit = await _client.GetAsync("/api/audit");
        Assert.Contains("settings.alerts", await audit.Content.ReadAsStringAsync());
    }

    // ---- 推送渠道：空字段协变臂 + 删除不存在 ----

    [Fact]
    public async Task Push_Channel_MinimalBody_And_DeleteMissing()
    {
        await AsAdmin();

        // 只传 url：Id/Name 走 ?? "" 空臂、Enabled 走 ?? true 默认臂。
        var res = await _client.PostAsJsonAsync("/api/push/channels", new { url = "https://example.com/hook" });
        Assert.True(res.IsSuccessStatusCode, $"minimal channel failed: {res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("enabled").GetBoolean());

        // 删除不存在的渠道 → 404（审计 not_found 臂）。
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.DeleteAsync("/api/push/channels/no-such-channel")).StatusCode);
    }

    // ---- 推送白名单：省略 entries 空臂 + 新增/移除审计两臂 ----

    [Fact]
    public async Task Push_Whitelist_OmitEntries_AddAndRemoveAudits()
    {
        await AsAdmin();

        // 省略 entries → 空数组臂。
        var empty = await _client.PutAsJsonAsync("/api/push/whitelist", new { });
        empty.EnsureSuccessStatusCode();
        Assert.Equal(0, (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());

        // 新增两条 → added 审计臂。
        var add = await _client.PutAsJsonAsync("/api/push/whitelist",
            new { entries = new[] { "https://a.example.com", "https://b.example.com" } });
        add.EnsureSuccessStatusCode();
        Assert.Equal(2, (await add.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());

        // 收缩为一条 → removed 审计臂。
        var shrink = await _client.PutAsJsonAsync("/api/push/whitelist",
            new { entries = new[] { "https://a.example.com" } });
        shrink.EnsureSuccessStatusCode();

        var audit = await _client.GetAsync("/api/audit");
        var auditText = await audit.Content.ReadAsStringAsync();
        Assert.Contains("push.whitelist", auditText);
        Assert.Contains("added", auditText);
        Assert.Contains("removed", auditText);
    }

    // ---- 外接建议配置：角色闸 + 非法 endpoint 校验 ----

    [Fact]
    public async Task AdviceExternal_Config_RoleGate_And_InvalidEndpoint()
    {
        var opUser = Rnd("extop");
        var opPass = "p4ssw0rd-extop";
        await CreateUserWithPassword("Operator", opUser, opPass);
        using var opClient = _factory.CreateClient();
        opClient.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(opUser, opPass));

        // Operator 读/写 → 403。
        Assert.Equal(HttpStatusCode.Forbidden,
            (await opClient.GetAsync("/api/advice/external/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await opClient.PostAsJsonAsync("/api/advice/external/config", new { endpoint = "https://x.example.com" })).StatusCode);

        // Admin 传非 URL endpoint → ArgumentException → 400，不落盘。
        await AsAdmin();
        var bad = await _client.PostAsJsonAsync("/api/advice/external/config", new { endpoint = "notaurl" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ---- K8s：建集群缺参 + 缺失集群操作 404/未知操作 400/未知模板 502 ----

    [Fact]
    public async Task K8s_AddCluster_MissingFields()
    {
        await AsAdmin();
        // name/kubeconfig 均省略 → ?? "" 空臂 + IsNullOrWhiteSpace 拦截 → 400，不发起连接测试。
        var res = await _client.PostAsJsonAsync("/api/k8s/clusters", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("name and kubeconfig required", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task K8s_Ops_MissingCluster_And_BogusOp()
    {
        await AsAdmin();
        const string missing = "no-such-cluster";

        // 各操作对不存在集群 → KeyNotFoundException → 404；省略 ns/deployment 走 ?? "default"/?? "" 空臂。
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/k8s/{missing}/ops",
            new { op = "restart" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/k8s/{missing}/ops",
            new { op = "rollout_status" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/k8s/{missing}/ops",
            new { op = "scale", replicas = "2" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/k8s/{missing}/ops",
            new { op = "remove", name = "app", confirm = true })).StatusCode);

        // deploy：模板合法但集群不存在 → 404；模板未知 → RenderException → 502。
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/k8s/{missing}/ops",
            new { op = "deploy", template = "nginx", name = "my-app" })).StatusCode);
        var badTemplate = await _client.PostAsJsonAsync($"/api/k8s/{missing}/ops",
            new { op = "deploy", name = "my-app" });
        Assert.Equal(HttpStatusCode.BadGateway, badTemplate.StatusCode);

        // 未知操作（策略默认 Admin 放行）→ switch 兜底臂 → 400。
        var bogus = await _client.PostAsJsonAsync($"/api/k8s/{missing}/ops", new { op = "bogus" });
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        Assert.Contains("未知操作", await bogus.Content.ReadAsStringAsync());
    }

    // ---- TOTP：端点空字段协变臂（专用户隔离，不碰共享 admin 状态） ----

    [Fact]
    public async Task Totp_Endpoints_MissingField_Arms()
    {
        // 未启用账号 enable 省略 code → ?? "" 空臂 → 400。
        var fresh = Rnd("totp3");
        var freshPass = "p4ssw0rd-totp3";
        await CreateUserWithPassword("Operator", fresh, freshPass);
        using var freshClient = _factory.CreateClient();
        freshClient.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(fresh, freshPass));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await freshClient.PostAsJsonAsync("/api/auth/totp/enable", new { username = fresh })).StatusCode);

        // status 省略 username → ?? "" 空臂 → enabled=false。
        var status = await _client.GetAsync("/api/auth/totp/status");
        Assert.True((await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean() is false);

        // login/verify/setup 省略字段 → ?? "" 空臂 → 401/401/401。
        using var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/auth/login", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/auth/totp/verify", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/auth/totp/setup", new { })).StatusCode);

        // regenerate 省略字段 → ?? "" 空臂 → 密码复验失败（空账号直接 401）。
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/auth/totp/recovery/regenerate", new { })).StatusCode);
    }

    // ---- 改密：省略旧密码 → 空臂 + old_password_mismatch 审计 ----

    [Fact]
    public async Task ChangePassword_MissingOldPassword_AuditsMismatch()
    {
        var user = Rnd("pwd3");
        var pass = "p4ssw0rd-pwd3";
        await CreateUserWithPassword("Operator", user, pass);
        using var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(user, pass));

        var res = await c.PostAsJsonAsync("/api/auth/change-password", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        await AsAdmin();
        var audit = await _client.GetAsync("/api/audit");
        Assert.Contains("old_password_mismatch", await audit.Content.ReadAsStringAsync());
    }
}
