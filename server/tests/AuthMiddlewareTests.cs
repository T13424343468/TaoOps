using Microsoft.AspNetCore.Http;
using TaoOps.Server.Auth;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/breakpoint-m3-security.md A4（401/403 鉴权）。</summary>
public class AuthMiddlewareTests
{
    private static DefaultHttpContext NewCtx(string method, string path, Action<DefaultHttpContext>? setup = null)
    {
        var ctx = new DefaultHttpContext
        {
            Request = { Method = method, Path = path },
            Response = { Body = new MemoryStream() },
        };
        setup?.Invoke(ctx);
        return ctx;
    }

    private static async Task<(bool NextCalled, DefaultHttpContext Ctx)> Invoke(
        TokenStore tokens, DefaultHttpContext ctx, Action<DefaultHttpContext>? setup = null)
    {
        var nextCalled = false;
        var mw = new AuthMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        setup?.Invoke(ctx);
        await mw.InvokeAsync(ctx, tokens);
        return (nextCalled, ctx);
    }

    [Fact]
    public async Task NoToken_OnApiPath_Returns401()
    {
        // A4：未带 token 访问 /api → 401，业务端点不执行。
        var (next, ctx) = await Invoke(new TokenStore(), NewCtx("POST", "/api/waf/rules"));
        Assert.False(next);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidOrExpiredToken_Returns401()
    {
        var (next, ctx) = await Invoke(new TokenStore(), NewCtx("POST", "/api/waf/rules"),
            setup: c => c.Request.Headers.Authorization = "Bearer not-a-real-token");
        Assert.False(next);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task OperatorToken_OnAdminPath_Returns403()
    {
        // A4：操作员角色调管理员接口 → 403。
        var tokens = new TokenStore();
        var session = tokens.Issue("op1", UserRole.Operator, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var (next, ctx) = await Invoke(tokens, NewCtx("GET", "/api/auth/users"),
            setup: c => c.Request.Headers.Authorization = $"Bearer {session.Token}");
        Assert.False(next);
        Assert.Equal(403, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AdminToken_PassesWithIdentityAttached()
    {
        var tokens = new TokenStore();
        var session = tokens.Issue("root", UserRole.Admin, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var (next, ctx) = await Invoke(tokens, NewCtx("POST", "/api/waf/rules"),
            setup: c => c.Request.Headers.Authorization = $"Bearer {session.Token}");
        Assert.True(next);
        Assert.Equal("root", ctx.Items["taoops.user"]);
        Assert.Equal("Admin", ctx.Items["taoops.role"]);
    }

    [Fact]
    public async Task NonApiPath_Anonymous_Passes()
    {
        // gRPC/根路径/WS 不经鉴权（M2 通道另有 TLS+TOFU）。
        var (next, _) = await Invoke(new TokenStore(), NewCtx("GET", "/"));
        Assert.True(next);
    }

    [Fact]
    public async Task LoginPath_Anonymous_Passes()
    {
        var (next, _) = await Invoke(new TokenStore(), NewCtx("POST", "/api/auth/login"));
        Assert.True(next);
    }
}
