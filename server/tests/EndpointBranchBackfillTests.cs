using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账补齐批次二：模块下发端点鉴权与异常分支——
/// 18 个 /api/nodes/{id}/* 端点的策略拒绝（Operator+空 op → 403）与
/// 未知节点（Admin+合法 op → BridgeException 404），外加建议/批量/用户管理/引导端点。
/// 含缺陷回归：角色枚举声明顺序非权限高低，批量和建议端点曾用枚举直接比较
/// 导致 Admin 被拒、ReadOnly 反放行（分支账补齐实拍抓出，已修 AtLeast 口径）。
/// </summary>
[Collection("Integration")]
public class EndpointBranchBackfillTests
{
    private readonly TaoOpsWebAppFactory _factory;
    private readonly HttpClient _client;

    public EndpointBranchBackfillTests(TaoOpsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AsAdmin()
    {
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.LoginAdminAsync());
    }

    private async Task AsOperator()
    {
        await AsAdmin();
        // 幂等创建操作员并登录（重名 400 忽略）。
        await _client.PostAsJsonAsync("/api/auth/users",
            new { username = "it-op-b2", password = "op2-s3cret-7", role = "Operator" });
        var res = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "it-op-b2", password = "op2-s3cret-7" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", body.GetProperty("token").GetString());
    }

    /// <summary>18 个模块下发端点 + 最小请求体（op 留空走策略默认拒绝）。</summary>
    public static TheoryData<string, object> ModuleEndpoints => new()
    {
        { "/api/nodes/{0}/docker", new { op = "" } },
        { "/api/nodes/{0}/webstats", new { op = "" } },
        { "/api/nodes/{0}/fw", new { op = "" } },
        { "/api/nodes/{0}/tamperguard", new { op = "" } },
        { "/api/nodes/{0}/cron", new { op = "" } },
        { "/api/nodes/{0}/site", new { op = "" } },
        { "/api/nodes/{0}/migrate", new { op = "" } },
        { "/api/nodes/{0}/apps", new { op = "" } },
        { "/api/nodes/{0}/lb", new { op = "" } },
        { "/api/nodes/{0}/logclean", new { op = "" } },
        { "/api/nodes/{0}/db", new { op = "" } },
        { "/api/nodes/{0}/db/ops", new { op = "" } },
        { "/api/nodes/{0}/ftp", new { op = "" } },
        { "/api/nodes/{0}/env", new { op = "" } },
        { "/api/nodes/{0}/ssl", new { op = "" } },
        { "/api/nodes/{0}/db/grants", new { op = "" } },
        { "/api/nodes/{0}/backup/policy", new { op = "" } },
        { "/api/nodes/{0}/files/edit", new { op = "" } },
    };

    [Theory]
    [MemberData(nameof(ModuleEndpoints))]
    public async Task ModuleEndpoints_UnknownOp_OperatorGets403(string pathTemplate, object body)
    {
        await AsOperator();
        var res = await _client.PostAsJsonAsync(string.Format(pathTemplate, "any-node"), body);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ModuleEndpoints))]
    public async Task ModuleEndpoints_ValidOp_UnknownNode_404(string pathTemplate, object body)
    {
        _ = body; // 请求体形状由 403 用例覆盖，此处只换合法 op 与不存在节点。
        await AsAdmin();
        // op=list 对 Admin 全放行；节点不存在走 BridgeException(404)。
        var res = await _client.PostAsJsonAsync(string.Format(pathTemplate, "ghost-node"),
            new Dictionary<string, object> { ["op"] = "list" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Cmd_UnknownNode_404()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/nodes/ghost-node/cmd",
            new { command = "uptime", args = Array.Empty<string>(), timeoutMs = 1000 });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- 缺陷回归：Admin 必须能过批量下发角色门（修复前被枚举顺序误拦 403）----

    [Fact]
    public async Task Batch_Admin_UnknownModule_BadRequest()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/batch",
            new { module = "no_such_module", args = Array.Empty<string>(), nodeIds = new[] { "n1" } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Batch_Admin_EmptyNodeIds_BadRequest()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/batch",
            new { module = "docker.manage", args = new[] { "list" }, nodeIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Batch_Admin_OfflineNodes_BadRequest()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/batch",
            new { module = "docker.manage", args = new[] { "list" }, nodeIds = new[] { "ghost-node" } });
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.BadRequest,
            $"got {res.StatusCode}: {body}");
        Assert.Contains("offline or unknown", body);
    }

    [Fact]
    public async Task Batch_ReadOnly_BlockedByMiddleware403()
    {
        await AsAdmin();
        await _client.PostAsJsonAsync("/api/auth/users",
            new { username = "it-viewer", password = "ro-s3cret-7", role = "ReadOnly" });
        var login = await _client.PostAsJsonAsync("/api/auth/login",
            new { username = "it-viewer", password = "ro-s3cret-7" });
        login.EnsureSuccessStatusCode();
        var lb = await login.Content.ReadFromJsonAsync<JsonElement>();
        using var ro = _factory.CreateClient();
        ro.DefaultRequestHeaders.Authorization = new("Bearer", lb.GetProperty("token").GetString());
        var res = await ro.PostAsJsonAsync("/api/batch",
            new { module = "docker.manage", args = new[] { "list" }, nodeIds = new[] { "n1" } });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- 缺陷回归：Admin 必须能查 AI 建议（修复前被枚举顺序误拦 403）----

    [Fact]
    public async Task Advice_Get_Admin_ReturnsAdvicesShape()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/advice/any-node");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("any-node", body.GetProperty("node_id").GetString());
        Assert.True(body.GetProperty("advices").ValueKind is JsonValueKind.Array);
    }

    [Fact]
    public async Task Advice_Analyze_Admin_NotConfigured_BadRequest()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/advice/any-node/analyze", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task AdviceExternalConfig_Get_AdminOk_NoKeyEcho()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/advice/external/config");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("has_key").GetBoolean());
    }

    [Fact]
    public async Task AdviceExternalConfig_Post_InvalidEndpoint_BadRequest()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/advice/external/config",
            new { endpoint = "not-a-valid-url", apiKey = "k", ssrfWhitelist = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task AuthUsers_CreateBadRole_BadRequest_AndDeleteUnknown_404()
    {
        await AsAdmin();
        var bad = await _client.PostAsJsonAsync("/api/auth/users",
            new { username = "u1", password = "password1", role = "SuperAdmin" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var del = await _client.DeleteAsync("/api/auth/users/no-such-user");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_WhenUsersExist_Forbidden403()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/auth/bootstrap",
            new { username = "another", password = "password1" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
