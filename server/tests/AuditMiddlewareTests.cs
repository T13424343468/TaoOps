using Microsoft.AspNetCore.Http;
using TaoOps.Server.Audit;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/breakpoint-m3-security.md（FR-D4 写操作自动留痕）。</summary>
public class AuditMiddlewareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taoops-auditm-" + Guid.NewGuid().ToString("N"));
    private AuditStore Store { get; }

    public AuditMiddlewareTests()
    {
        Directory.CreateDirectory(_dir);
        Store = new AuditStore(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WriteApiCall_IsAudited_WithUserAndResult()
    {
        var mw = new AuditMiddleware(_ =>
        {
            // 业务处理：模拟鉴权中间件已挂身份。
            return Task.CompletedTask;
        });
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/api/waf/rules";
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.1.2.3");
        ctx.Items["taoops.user"] = "root";
        ctx.Items["taoops.role"] = "Admin";
        ctx.Response.StatusCode = 200;
        await mw.InvokeAsync(ctx, Store);

        var ev = Assert.Single(Store.Query());
        Assert.Equal("root", ev.User);
        Assert.Equal("POST", ev.Action);
        Assert.Equal("/api/waf/rules", ev.Target);
        Assert.Equal("200", ev.Result);
        Assert.Equal("10.1.2.3", ev.Ip);
    }

    [Fact]
    public async Task UnauthorizedWrite_IsAlsoAudited()
    {
        // 401 拒绝也留痕（匿名探试可追溯）。
        var mw = new AuditMiddleware(_ => Task.CompletedTask);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "DELETE";
        ctx.Request.Path = "/api/waf/rules/x";
        ctx.Response.StatusCode = 401;
        await mw.InvokeAsync(ctx, Store);

        var ev = Assert.Single(Store.Query());
        Assert.Equal("(anonymous)", ev.User);
        Assert.Equal("401", ev.Result);
    }

    [Fact]
    public async Task GetAndNonApi_Skipped()
    {
        var mw = new AuditMiddleware(_ => Task.CompletedTask);
        var get = new DefaultHttpContext();
        get.Request.Method = "GET";
        get.Request.Path = "/api/waf/rules";
        await mw.InvokeAsync(get, Store);
        var root = new DefaultHttpContext();
        root.Request.Method = "POST";
        root.Request.Path = "/";
        await mw.InvokeAsync(root, Store);
        Assert.Empty(Store.Query());
    }
}
