using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>改密码端点集成测试（backlog F2）：旧密码校验、新密码长度、改后可登录、审计留痕。
/// 用独立测试用户，不轮换共享管理员口令（夹具全运行期共用）。</summary>
[Collection("Integration")]
public class ChangePasswordEndpointTests
{
    private const string Username = "it-pwd-user";
    private const string InitialPassword = "init-pass-11";
    private const string RotatedPassword = "rotated-pass-77";

    private readonly TaoOpsWebAppFactory _factory;
    private readonly HttpClient _client;

    public ChangePasswordEndpointTests(TaoOpsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AsAdmin() =>
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.LoginAdminAsync());

    private async Task EnsureUserAsync()
    {
        await AsAdmin();
        var create = await _client.PostAsJsonAsync("/api/auth/users",
            new { username = Username, password = InitialPassword, role = "Operator" });
        Assert.True(create.IsSuccessStatusCode || create.StatusCode == HttpStatusCode.BadRequest,
            $"create user failed: {create.StatusCode}");
    }

    private async Task<string> LoginAsync(string password)
    {
        using var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = Username, password });
        return res.StatusCode == HttpStatusCode.OK
            ? (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!
            : "";
    }

    [Fact]
    public async Task Anonymous_Rejected401()
    {
        var res = await _client.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = "x", newPassword = "y" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task WrongOldPassword_BadRequest_AndAudited()
    {
        await EnsureUserAsync();
        var token = await LoginAsync(InitialPassword);
        Assert.NotEmpty(token);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await _client.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = "definitely-wrong", newPassword = RotatedPassword });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        await AsAdmin();
        var audit = await _client.GetStringAsync("/api/audit?limit=100");
        Assert.Contains("old_password_mismatch", audit);
    }

    [Fact]
    public async Task ShortNewPassword_BadRequest()
    {
        await EnsureUserAsync();
        var token = await LoginAsync(InitialPassword);
        Assert.NotEmpty(token);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await _client.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = InitialPassword, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ValidChange_ThenLoginWithNewPassword()
    {
        await EnsureUserAsync();
        var token = await LoginAsync(InitialPassword);
        Assert.NotEmpty(token);
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await _client.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = InitialPassword, newPassword = RotatedPassword });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // 旧密码登录失败、新密码登录成功。
        Assert.Empty(await LoginAsync(InitialPassword));
        Assert.NotEmpty(await LoginAsync(RotatedPassword));
    }
}
