using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using TaoOps.Server.Services.K8s;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次四第六波（方案 C 可达臂补齐）：TOTP 端点缺参臂（verify/setup/enable/
/// regenerate 的 ?? null 臂）、建号缺用户名臂、改密空新密码臂、外接配置保存成功臂、
/// K8s 接入缺 kubeconfig/坏 kubeconfig/confirm 卸载缺失集群、ops 非法 replicas/
/// 缺 op/缺名卸载/无 deployment 重启审计臂、mock 故障 502 臂。
/// </summary>
[Collection("Integration")]
public class Batch4Wave6AuthTests
{
    private readonly TaoOpsWebAppFactory _factory;
    private readonly HttpClient _client;

    public Batch4Wave6AuthTests(TaoOpsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AsAdmin() =>
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.LoginAdminAsync());

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

    // ---- TOTP 端点缺参臂：?? null 兜底走真实请求可达 ----

    [Fact]
    public async Task TotpVerify_MissingUsername_Returns401()
    {
        await AsAdmin();
        // username 缺省 → "" → 账号不存在 → 401（req.Username ?? "" null 臂）。
        var res = await _client.PostAsJsonAsync("/api/auth/totp/verify", new { code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task TotpSetup_MissingPasswordOrUsername_Returns401()
    {
        var known = $"w6-setup-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var password = "p4ssw0rd-w6setup";
        await CreateUserWithPassword("Operator", known, password);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(known, password));

        // 密码缺省 → 复验失败 → 401（req.Password ?? "" null 臂）。
        var noPass = await _client.PostAsJsonAsync("/api/auth/totp/setup", new { username = known });
        Assert.Equal(HttpStatusCode.Unauthorized, noPass.StatusCode);

        // 用户名缺省 → 复验用户不存在 → 401（req.Username ?? "" null 臂）。
        var noUser = await _client.PostAsJsonAsync("/api/auth/totp/setup", new { password });
        Assert.Equal(HttpStatusCode.Unauthorized, noUser.StatusCode);
    }

    [Fact]
    public async Task TotpEnable_MissingCodeOrUsername_Returns400()
    {
        var known = $"w6-enable-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var password = "p4ssw0rd-w6enable";
        await CreateUserWithPassword("Operator", known, password);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(known, password));

        // code 缺省 → "" → 码无效 → 400（req.Code ?? "" null 臂）。
        var noCode = await _client.PostAsJsonAsync("/api/auth/totp/enable", new { username = known });
        Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);

        // username 缺省 → 未绑定 → 400 "totp not set up"（req.Username ?? "" null 臂）。
        var noUser = await _client.PostAsJsonAsync("/api/auth/totp/enable", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, noUser.StatusCode);
        Assert.Contains("totp not set up", await noUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TotpRegenerate_WrongPassword_OrMissingFields_Returns400()
    {
        var known = $"w6-regen-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var password = "p4ssw0rd-w6regen";
        await CreateUserWithPassword("Operator", known, password);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(known, password));

        // 密码错误 → 复验失败 → 400（非空参数臂）。
        var wrong = await _client.PostAsJsonAsync("/api/auth/totp/recovery/regenerate",
            new { username = known, password = "wrong-pass" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // 字段全缺 → 复验失败 → 400（?? null 臂）。
        var empty = await _client.PostAsJsonAsync("/api/auth/totp/recovery/regenerate", new { });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    // ---- 用户管理：缺用户名臂 ----

    [Fact]
    public async Task CreateUser_MissingUsername_UsesEmptyFallback()
    {
        await AsAdmin();
        // username 缺省 → "" 兜底（req.Username ?? "" null 臂）；首次落库成功或重名 400 均算达臂。
        var res = await _client.PostAsJsonAsync("/api/auth/users",
            new { password = "p4ssw0rd-x", role = "ReadOnly" });
        Assert.True(res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.BadRequest,
            await res.Content.ReadAsStringAsync());
    }

    // ---- 修改密码：空新密码臂 + 删号后旧 token 404 臂 ----

    [Fact]
    public async Task ChangePassword_EmptyNewPassword_Returns400()
    {
        var known = $"w6-cpwd-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var password = "p4ssw0rd-w6cpwd";
        await CreateUserWithPassword("Operator", known, password);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(known, password));

        // 新密码空串 → IsNullOrEmpty true 短路臂 → 400。
        var empty = await _client.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = password, newPassword = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("至少 8 位", await empty.Content.ReadAsStringAsync());

        // 新密码字段缺省 → null → 同臂。
        var missing = await _client.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = password });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_UserDeleted_TokenStillValid_Returns404()
    {
        var known = $"w6-del-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var password = "p4ssw0rd-w6del";
        await CreateUserWithPassword("Operator", known, password);
        var token = await LoginAs(known, password);

        // 管理员删号（UserStore.Remove 不吊销会话）→ 旧 token 仍可达端点 → 404 臂。
        await AsAdmin();
        var del = await _client.DeleteAsync($"/api/auth/users/{known}");
        Assert.True(del.IsSuccessStatusCode, await del.Content.ReadAsStringAsync());

        using var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var res = await userClient.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = password, newPassword = "p4ssw0rd-new9" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- 外接配置：保存成功臂（合法 endpoint，缺 whitelist/apiKey 兜底） ----

    [Fact]
    public async Task AdviceExternalConfig_SaveSuccess_WithoutOptionalFields()
    {
        await AsAdmin();
        // 合法 endpoint + 缺省 apiKey/ssrfWhitelist → ?? "" / ?? [] 兜底臂 → 200 ok。
        var res = await _client.PostAsJsonAsync("/api/advice/external/config",
            new { endpoint = "https://x.example.com" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.True((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        // 不带 apiKey → has_key 保持 false（不破坏其他用例的状态假设）。
        var get = await _client.GetAsync("/api/advice/external/config");
        get.EnsureSuccessStatusCode();
        Assert.False((await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("has_key").GetBoolean());

        // endpoint 缺省 → "" → 校验失败 → 400（req.Endpoint ?? "" null 臂）。
        var noEndpoint = await _client.PostAsJsonAsync("/api/advice/external/config", new { apiKey = "k" });
        Assert.Equal(HttpStatusCode.BadRequest, noEndpoint.StatusCode);
    }
}

/// <summary>批次四第六波 K8s 侧：接入缺参/坏 kubeconfig、confirm 卸载、ops 缺参与故障臂。</summary>
[Collection("Integration")]
public class Batch4Wave6K8sTests : IDisposable
{
    private readonly K8sFakeApiFactory _factory;
    private readonly HttpClient _client;
    private readonly string _dataDir;
    private readonly List<string> _seeded = new();

    public Batch4Wave6K8sTests()
    {
        _factory = new K8sFakeApiFactory();
        _factory.Api.StartAsync().GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", _factory.LoginAdminAsync().GetAwaiter().GetResult());
        _dataDir = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR")!;
    }

    public void Dispose()
    {
        foreach (var name in _seeded) new KubeClusterStore(_dataDir).Remove(name, confirm: true);
        _factory.Dispose();
        _factory.Api.DisposeAsync().GetAwaiter().GetResult();
    }

    private void SeedCluster(string name)
    {
        new KubeClusterStore(_dataDir).Save(name, _factory.Api.Kubeconfig, "v1.29.0");
        _seeded.Add(name);
    }

    private async Task<string> LoginAs(string username, string password)
    {
        using var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username, password });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    private async Task AsAdmin() =>
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.LoginAdminAsync());

    [Fact]
    public async Task AddCluster_MissingKubeconfig_Returns400()
    {
        // name 合法、kubeconfig 缺省 → IsNullOrWhiteSpace 第二条件 true 臂 → 400。
        var res = await _client.PostAsJsonAsync("/api/k8s/clusters", new { name = "w6-no-kc" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("name and kubeconfig required", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AddCluster_BadKubeconfig_Returns400_AndAudits()
    {
        // 坏 kubeconfig → TestVersion 抛 → catch → 400 + 审计 connect_failed。
        var res = await _client.PostAsJsonAsync("/api/k8s/clusters",
            new { name = "w6-bad-kc", kubeconfig = "not: [a, valid, kubeconfig" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("连接测试失败", await res.Content.ReadAsStringAsync());

        var audit = await _client.GetAsync("/api/audit?limit=50");
        audit.EnsureSuccessStatusCode();
        Assert.Contains("connect_failed", await audit.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RemoveCluster_ConfirmTrue_Missing_Returns404()
    {
        // confirm=true 且集群不存在 → not_found 臂 → 404（三元 confirm 臂）。
        var res = await _client.DeleteAsync("/api/k8s/clusters/w6-never-existed?confirm=true");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("cluster not found", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ops_Scale_NonNumericReplicas_AndMissingArgs_Returns400()
    {
        SeedCluster("w6-scale-bad");
        // replicas 非数字 → TryParse false → -1 → 越界 ArgumentException → 400；
        // ns/deployment 缺省 → ?? "default" / ?? "" 兜底臂。
        var res = await _client.PostAsJsonAsync("/api/k8s/w6-scale-bad/ops",
            new { op = "scale", replicas = "abc" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("replicas", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ops_MissingOp_Returns400()
    {
        SeedCluster("w6-noop");
        // op 缺省 → "" → 未知操作 → ArgumentException → 400（req.Op ?? "" null 臂 + default 臂）。
        var res = await _client.PostAsJsonAsync("/api/k8s/w6-noop/ops", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("未知操作", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ops_Restart_WithoutDeployment_AuditsEmptyTarget()
    {
        SeedCluster("w6-restart-bare");
        // deployment/name 全缺 → 审计 Target 兜底臂（req.Deployment ?? req.Name 双 null）。
        var res = await _client.PostAsJsonAsync("/api/k8s/w6-restart-bare/ops",
            new { op = "restart" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());

        var audit = await _client.GetAsync("/api/audit?limit=50");
        audit.EnsureSuccessStatusCode();
        Assert.Contains("w6-restart-bare/restart/", await audit.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ops_Remove_WithoutName_SucceedsWithEmptyLabel()
    {
        SeedCluster("w6-remove-bare");
        // name 缺省 → "" 标签查询空集 → (0,0) 成功（req.Name ?? "" null 臂）。
        var res = await _client.PostAsJsonAsync("/api/k8s/w6-remove-bare/ops",
            new { op = "remove", confirm = true });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ops_Deploy_WithoutNameAndNs_UsesFallbacks()
    {
        SeedCluster("w6-deploy-bare");
        // name/ns 缺省 → ?? "" 兜底臂可达；空名渲染校验抛 RenderException → 502。
        var res = await _client.PostAsJsonAsync("/api/k8s/w6-deploy-bare/ops",
            new { op = "deploy", template = "nginx" });
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Contains("应用名不合法", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ops_MockApiError_Returns502()
    {
        SeedCluster("w6-502");
        // mock API 500 → Scale 抛 HttpOperationException → catch Exception → 502 臂。
        _factory.Api.OnJson("PATCH", "/apis/apps/v1/namespaces/default/deployments/web/scale",
            """{"kind":"Status","status":"Failure"}""", status: 500);
        var res = await _client.PostAsJsonAsync("/api/k8s/w6-502/ops",
            new { op = "scale", ns = "default", deployment = "web", replicas = "2" });
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }

    // ---- 角色闸：Operator 写操作 403 臂（Allows-false 可达路径） ----

    [Fact]
    public async Task Operator_K8sWrites_Returns403_WithDeniedAudit()
    {
        var opUser = $"w6-op-{Guid.NewGuid():N}".ToLowerInvariant()[..22];
        var opPass = "p4ssw0rd-w6op";
        await AsAdmin();
        var create = await _client.PostAsJsonAsync("/api/auth/users",
            new { username = opUser, password = opPass, role = "Operator" });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());

        using var opClient = _factory.CreateClient();
        opClient.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAs(opUser, opPass));

        // 接入集群 → 403（Allows("add", Operator) false 臂）。
        var add = await opClient.PostAsJsonAsync("/api/k8s/clusters",
            new { name = "w6-op-add", kubeconfig = "whatever" });
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);

        // 卸载集群 → 403（Allows("remove_cluster", Operator) false 臂）。
        var remove = await opClient.DeleteAsync("/api/k8s/clusters/w6-op-add?confirm=true");
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);

        // ops 变更 → 403 + 审计 denied（Allows(req.Op) false 臂 + denied 审计臂）。
        SeedCluster("w6-op-target");
        var ops = await opClient.PostAsJsonAsync("/api/k8s/w6-op-target/ops",
            new { op = "scale", ns = "default", deployment = "web", replicas = "2" });
        Assert.Equal(HttpStatusCode.Forbidden, ops.StatusCode);

        var audit = await _client.GetAsync("/api/audit?limit=50");
        audit.EnsureSuccessStatusCode();
        Assert.Contains("denied", await audit.Content.ReadAsStringAsync());
    }
}
