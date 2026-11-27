using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using TaoOps.Server.Services.K8s;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次二第二波：TOTP 端点全路径、用户管理、推送渠道/白名单/测试推送、
/// K8s 端点鉴权与错误路径、指标窗口参数、DockerOpArgs 摊平分支。
/// </summary>
[Collection("Integration")]
public class ProgramBranchBackfill3Tests
{
    private readonly TaoOpsWebAppFactory _factory;
    private readonly HttpClient _client;

    public ProgramBranchBackfill3Tests(TaoOpsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AsAdmin()
    {
        // 诊断：登录失败时带出失败原因与锁定名单，避免连锁 423 掩盖首因。
        try
        {
            _client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.LoginAdminAsync());
        }
        catch (Exception ex)
        {
            string lockouts = "";
            try
            {
                var res = await _client.GetAsync("/api/auth/lockouts");
                lockouts = await res.Content.ReadAsStringAsync();
            }
            catch { }
            throw new Exception($"admin login failed: {ex.Message}; lockouts={lockouts}");
        }
    }

    /// <summary>经端点建号并返回明文口令（测试专用随机后缀防撞）。</summary>
    private async Task<string> CreateUser(string role, string prefix)
    {
        var username = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant()[..24];
        return await CreateUserWithPassword(role, username, "p4ssw0rd-" + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>经端点建号（口令已知，供后续登录）；必须先 AsAdmin，避免直写账号库绕过管理员引导。</summary>
    private async Task<string> CreateUserWithPassword(string role, string username, string password)
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/auth/users",
            new { username, password, role });
        Assert.True(res.IsSuccessStatusCode, $"create user failed: {res.StatusCode}");
        return username;
    }

    private async Task<string> LoginAs(string username, string password)
    {
        using var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username, password });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    // ---- TOTP：RFC6238 同口径算码（与 TotpTests 同一算法） ----
    private static string ComputeCode(string secret, long step)
    {
        var key = Totp.Base32Decode(secret)!;
        var counter = new byte[8];
        var s = step;
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(s & 0xFF); s >>= 8; }
        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public async Task Totp_FullLifecycle_ThroughEndpoints()
    {
        // 已知口令账号：经端点创建（先 AsAdmin 完成引导，避免直写账号库）。
        var known = $"totp-known-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var password = "p4ssw0rd-totptest";
        await CreateUserWithPassword("Operator", known, password);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(known, password));

        // 状态查询：未启用。
        var status0 = await client.GetAsync($"/api/auth/totp/status?username={known}");
        Assert.True((await status0.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean() is false);

        // 密码复验失败 → 401。
        var badSetup = await client.PostAsJsonAsync("/api/auth/totp/setup", new { username = known, password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, badSetup.StatusCode);

        // 正确密码 → 密钥与 otpauth URI。
        var setup = await client.PostAsJsonAsync("/api/auth/totp/setup", new { username = known, password });
        setup.EnsureSuccessStatusCode();
        var setupBody = await setup.Content.ReadFromJsonAsync<JsonElement>();
        var secret = setupBody.GetProperty("secret").GetString()!;
        Assert.Contains("otpauth://", setupBody.GetProperty("otpauth_uri").GetString());

        // 未 setup 的账号 enable → 400（自建已知口令账号登录后调用）。
        var stranger = $"totpstranger-{Guid.NewGuid():N}".ToLowerInvariant()[..24];
        var strangerPass = "p4ssw0rd-stranger";
        await CreateUserWithPassword("Operator", stranger, strangerPass);
        var strangerToken = await LoginAs(stranger, strangerPass);
        using var noSetup = _factory.CreateClient();
        noSetup.DefaultRequestHeaders.Authorization = new("Bearer", strangerToken);
        var enableNoSetup = await noSetup.PostAsJsonAsync("/api/auth/totp/enable", new { username = stranger, code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, enableNoSetup.StatusCode);

        var badEnable = await client.PostAsJsonAsync("/api/auth/totp/enable", new { username = known, code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, badEnable.StatusCode);

        // 正确码 → 绑定成功，返回 10 枚恢复码。
        var code = ComputeCode(secret, NowMs() / 1000 / 30);
        var enable = await client.PostAsJsonAsync("/api/auth/totp/enable", new { username = known, code });
        Assert.True(enable.IsSuccessStatusCode,
            $"enable failed: {enable.StatusCode} {await enable.Content.ReadAsStringAsync()}");
        var codes = (await enable.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("recovery_codes").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(10, codes.Count);

        // 已启用后：密码登录 → totp_required，不发 token。
        var loginRes = await client.PostAsJsonAsync("/api/auth/login", new { username = known, password });
        var loginBody = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(loginBody.GetProperty("totp_required").GetBoolean());

        // 状态查询：已启用。
        var status1 = await client.GetAsync($"/api/auth/totp/status?username={known}");
        Assert.True((await status1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());

        // 二次校验错码 → 401；恢复码 → 200 发会话。
        var badVerify = await client.PostAsJsonAsync("/api/auth/totp/verify", new { username = known, code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, badVerify.StatusCode);
        var recVerify = await client.PostAsJsonAsync("/api/auth/totp/verify", new { username = known, code = codes[0] });
        recVerify.EnsureSuccessStatusCode();
        Assert.True((await recVerify.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("token", out _));

        // 未启用账号 verify → 401。
        var strangerVerify = await client.PostAsJsonAsync("/api/auth/totp/verify", new { username = stranger, code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, strangerVerify.StatusCode);

        // 重新生成恢复码：错密码 400，对密码 200。
        var badRegen = await client.PostAsJsonAsync("/api/auth/totp/recovery/regenerate",
            new { username = known, password = "wrong" });
        Assert.Equal(HttpStatusCode.BadRequest, badRegen.StatusCode);
        var regen = await client.PostAsJsonAsync("/api/auth/totp/recovery/regenerate",
            new { username = known, password });
        regen.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Totp_Verify_Locked_AfterRepeatedFailures_423()
    {
        var password = "p4ssw0rd-lock";
        var known = $"totplock-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        await CreateUserWithPassword("Operator", known, password);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(known, password));
        var setup = await client.PostAsJsonAsync("/api/auth/totp/setup", new { username = known, password });
        setup.EnsureSuccessStatusCode();
        var secret = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var code = ComputeCode(secret, NowMs() / 1000 / 30);
        var enable = await client.PostAsJsonAsync("/api/auth/totp/enable", new { username = known, code });
        Assert.True(enable.IsSuccessStatusCode,
            $"enable failed: {enable.StatusCode} {await enable.Content.ReadAsStringAsync()}");

        // 夹具口径：2 次失败即锁。错码两次后 verify → 423。
        await client.PostAsJsonAsync("/api/auth/totp/verify", new { username = known, code = "000000" });
        await client.PostAsJsonAsync("/api/auth/totp/verify", new { username = known, code = "000001" });
        var locked = await client.PostAsJsonAsync("/api/auth/totp/verify", new { username = known, code = "000002" });
        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
        Assert.Contains("account locked", await locked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_Locked_AfterRepeatedFailures_423()
    {
        var password = "p4ssw0rd-lock2";
        var known = $"loginlock-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        await CreateUserWithPassword("Operator", known, password);

        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/login", new { username = known, password = "nope1" });
        await client.PostAsJsonAsync("/api/auth/login", new { username = known, password = "nope2" });
        var locked = await client.PostAsJsonAsync("/api/auth/login", new { username = known, password });
        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
    }

    [Fact]
    public async Task Auth_Me_And_Logout_WithAndWithoutBearer()
    {
        await AsAdmin();
        var me = await _client.GetAsync("/api/auth/me");
        Assert.True(me.IsSuccessStatusCode,
            $"me failed: {me.StatusCode} {await me.Content.ReadAsStringAsync()} auth={_client.DefaultRequestHeaders.Authorization}");
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TaoOpsWebAppFactory.AdminUser, body.GetProperty("user").GetString());

        // 带 Bearer 登出 → 200；不带 Bearer 前缀的 raw 分支（裸 token）→ 200。
        var token = await _factory.LoginAdminAsync();
        using var c1 = _factory.CreateClient();
        c1.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var logout1 = await c1.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout1.StatusCode);
        using var c2 = _factory.CreateClient();
        c2.DefaultRequestHeaders.Add("Authorization", await _factory.LoginAdminAsync());
        var logout2 = await c2.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout2.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_UserMissing_404()
    {
        // 为不在账号库的用户签发会话 → 改密码 404。
        var tokens = _factory.Services.GetRequiredService<TokenStore>();
        var session = tokens.Issue("ghost-user", UserRole.Admin, NowMs());
        using var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", session.Token);
        var res = await c.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = "x", newPassword = "longenough1" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Users_CreateDelete_Lockouts_List()
    {
        await AsAdmin();
        var username = $"deluser-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var create = await _client.PostAsJsonAsync("/api/auth/users",
            new { username, password = "p4ssw0rd-x1", role = "Operator" });
        create.EnsureSuccessStatusCode();

        var lockouts = await _client.GetAsync("/api/auth/lockouts");
        lockouts.EnsureSuccessStatusCode();

        var del = await _client.DeleteAsync($"/api/auth/users/{username}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    // ---- 推送端点 ----

    [Fact]
    public async Task Push_Channels_Delete_And_TestPush_Paths()
    {
        await AsAdmin();
        // 端点用的是 DI 单例存储（内存态），测试直插也必须打在同一实例上。
        var channels = _factory.Services.GetRequiredService<Push.PushChannelStore>();

        // 测试推送：渠道不存在 → 404。
        var missing = await _client.PostAsJsonAsync("/api/push/test", new { channelId = "no-such" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // 内网地址渠道（SSRF 拒绝）→ 测试推送 400。
        // 渠道创建端点会先过 SSRF 闸，内网地址进不去；这里走存储直插（Id 留空走新增分支）。
        var added = channels.Upsert(new Push.PushChannel { Id = "", Name = "n", Url = "https://10.0.0.5/hook", Enabled = true });
        var id = added.Id;
        Assert.False(string.IsNullOrEmpty(id));
        var ssrf = await _client.PostAsJsonAsync("/api/push/test", new { channelId = id });
        Assert.Equal(HttpStatusCode.BadRequest, ssrf.StatusCode);

        // 删除：存在 → 200；不存在 → 404。
        var del = await _client.DeleteAsync($"/api/push/channels/{id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var delAgain = await _client.DeleteAsync($"/api/push/channels/{id}");
        Assert.Equal(HttpStatusCode.NotFound, delAgain.StatusCode);
    }

    [Fact]
    public async Task Push_Whitelist_Replace_AuditsAddedAndRemoved()
    {
        await AsAdmin();
        var put1 = await _client.PutAsJsonAsync("/api/push/whitelist",
            new { entries = new[] { "hook.example.com", "hooks2.example.com" } });
        put1.EnsureSuccessStatusCode();
        // 再替换为空表 → 移除分支。
        var put2 = await _client.PutAsJsonAsync("/api/push/whitelist", new { entries = Array.Empty<string>() });
        put2.EnsureSuccessStatusCode();
        var list = await _client.GetAsync("/api/push/whitelist");
        Assert.Equal("[]", await list.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Push_Log_Query_WithFilters()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/push/log?channelId=c1&from=1&to=2");
        res.EnsureSuccessStatusCode();
    }

    // ---- 权限清单 / 指标窗口 / 建议 ----

    [Fact]
    public async Task Permissions_Matrix_And_MetricsHours_Branches()
    {
        await AsAdmin();
        var perm = await _client.GetAsync("/api/auth/permissions");
        perm.EnsureSuccessStatusCode();
        var body = await perm.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Admin", body.GetProperty("role").GetString());

        // hours=24 与非法值（回落 1）两条分支。
        var m24 = await _client.GetAsync("/api/nodes/any/metrics?hours=24");
        m24.EnsureSuccessStatusCode();
        var mBad = await _client.GetAsync("/api/nodes/any/metrics?hours=7");
        mBad.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Advice_And_ExternalConfig_RoleGates()
    {
        await AsAdmin();
        var advice = await _client.GetAsync("/api/advice/any-node");
        advice.EnsureSuccessStatusCode();

        // 只读角色：外接配置读写均 403（端点级 Admin 闸）。
        var roUser = $"ro-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var roPass = "p4ssw0rd-ro";
        await CreateUserWithPassword("ReadOnly", roUser, roPass);
        using var ro = _factory.CreateClient();
        ro.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(roUser, roPass));
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.GetAsync("/api/advice/external/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.PostAsJsonAsync("/api/advice/external/config",
            new { endpoint = "https://x.example.com", apiKey = "k" })).StatusCode);
    }

    // ---- K8s 端点 ----

    [Fact]
    public async Task K8s_AddCluster_MissingFields_400()
    {
        await AsAdmin();
        var noName = await _client.PostAsJsonAsync("/api/k8s/clusters", new { name = "", kubeconfig = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        var noKc = await _client.PostAsJsonAsync("/api/k8s/clusters", new { name = "n", kubeconfig = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noKc.StatusCode);
    }

    [Fact]
    public async Task K8s_DeleteCluster_ConfirmPaths()
    {
        await AsAdmin();
        var kc = """
            apiVersion: v1
            kind: Config
            current-context: c1
            contexts:
            - name: c1
              cluster: cl
            clusters:
            - name: cl
              cluster:
                server: https://10.0.0.9:6443
            """;
        new KubeClusterStore(_factory.DataDir).Save("seed-del", kc, "v1.0.0");

        // 无 confirm → 400；带 confirm → 200；不存在带 confirm → 404。
        var noConfirm = await _client.DeleteAsync("/api/k8s/clusters/seed-del");
        Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        var confirmed = await _client.DeleteAsync("/api/k8s/clusters/seed-del?confirm=true");
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var ghost = await _client.DeleteAsync("/api/k8s/clusters/ghost?confirm=true");
        Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
    }

    [Fact]
    public async Task K8s_Workloads_Events_Metrics_UnknownCluster_404()
    {
        await AsAdmin();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/k8s/nope/workloads")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/k8s/nope/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/k8s/nope/metrics")).StatusCode);
    }

    [Fact]
    public async Task K8s_Ops_Templates_ScaleBadReplicas_And_RoleGate()
    {
        await AsAdmin();
        // templates：无需集群，直接返回模板清单。
        var templates = await _client.PostAsJsonAsync("/api/k8s/any/ops", new { op = "templates" });
        templates.EnsureSuccessStatusCode();

        // replicas 非数字：TryParse 失败 → -1，先触发范围校验（0-100）→ 400（早于集群存在性检查）。
        var badReplicas = await _client.PostAsJsonAsync("/api/k8s/nope/ops",
            new { op = "scale", ns = "default", deployment = "web", replicas = "abc" });
        Assert.Equal(HttpStatusCode.BadRequest, badReplicas.StatusCode);

        // 操作员：中间件放行（非只读），端点策略拒绝 → 403。
        var opUser = $"op2-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var opPass = "p4ssw0rd-op";
        await CreateUserWithPassword("Operator", opUser, opPass);
        using var op = _factory.CreateClient();
        op.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(opUser, opPass));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await op.PostAsJsonAsync("/api/k8s/clusters", new { name = "n", kubeconfig = "k" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await op.PostAsJsonAsync("/api/k8s/any/ops", new { op = "scale", replicas = "1" })).StatusCode);
    }

    // ---- DockerOpArgs 摊平分支（直测） ----

    [Fact]
    public void DockerOpArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = DockerOpArgs.Build(new DockerOpRequest(
            "registry_login", "n1", "ref1", "cmd", 50, "addr", "reg", "user", "pass", 0));
        Assert.Contains("op=registry_login", args);
        Assert.Contains("name=n1", args);
        Assert.Contains("ref=ref1", args);
        Assert.Contains("command=cmd", args);
        Assert.Contains("addr=addr", args);
        Assert.Contains("registry=reg", args);
        Assert.Contains("username=user", args);
        Assert.Contains("password=pass", args);
        Assert.Contains("tail=50", args);
    }

    [Fact]
    public void DockerOpArgs_AllEmpty_EmitsOpOnly()
    {
        var args = DockerOpArgs.Build(new DockerOpRequest("ps", null, null, null, null, null, null, null, null, 0));
        Assert.Single(args);
        Assert.Equal("op=ps", args[0]);
    }
}
