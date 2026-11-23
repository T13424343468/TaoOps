using Microsoft.AspNetCore.Http;
using TaoOps.Server.Auth;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/breakpoint-m3-enterprise.md A12-A13（只读角色 + 权限矩阵）。</summary>
public class RbacTests
{
    private static DefaultHttpContext NewCtx(string method, string path)
    {
        return new DefaultHttpContext
        {
            Request = { Method = method, Path = path },
            Response = { Body = new MemoryStream() },
        };
    }

    private static async Task<(bool NextCalled, DefaultHttpContext Ctx)> Invoke(TokenStore tokens, DefaultHttpContext ctx)
    {
        var nextCalled = false;
        var mw = new AuthMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        await mw.InvokeAsync(ctx, tokens);
        return (nextCalled, ctx);
    }

    private static async Task<(bool NextCalled, DefaultHttpContext Ctx)> InvokeAs(
        UserRole role, string method, string path)
    {
        var tokens = new TokenStore();
        var session = tokens.Issue("u", role, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var ctx = NewCtx(method, path);
        ctx.Request.Headers.Authorization = $"Bearer {session.Token}";
        return await Invoke(tokens, ctx);
    }

    private static readonly string[] WriteMethods = ["POST", "PUT", "DELETE", "PATCH"];
    private static readonly string[] ReadMethods = ["GET", "HEAD"];

    // ---- A12：ReadOnly 写方法一律 403，读方法放行 ----

    [Fact]
    public async Task ReadOnly_WriteMethods_AnyApiPath_Returns403()
    {
        // 越权矩阵：写方法 × 代表性端点（含 AdminOnly 前缀与普通业务端点）。
        string[] paths =
        [
            "/api/auth/users", "/api/audit/export", "/api/waf/rules",
            "/api/nodes/cmd", "/api/files/upload", "/api/backup/run",
        ];
        foreach (var path in paths)
        foreach (var method in WriteMethods)
        {
            var (next, ctx) = await InvokeAs(UserRole.ReadOnly, method, path);
            Assert.False(next, $"{method} {path}");
            Assert.True(ctx.Response.StatusCode == 403, $"{method} {path} → {ctx.Response.StatusCode}");
        }
    }

    [Fact]
    public async Task ReadOnly_ReadMethods_NonAdminPath_Passes()
    {
        foreach (var method in ReadMethods)
        {
            var (next, ctx) = await InvokeAs(UserRole.ReadOnly, method, "/api/nodes");
            Assert.True(next, $"{method} /api/nodes");
            Assert.Equal("ReadOnly", ctx.Items["taoops.role"]);
        }
    }

    [Fact]
    public async Task ReadOnly_ReadMethod_OnAdminPath_Returns403()
    {
        // 只读角色读管理员接口仍受 AdminOnly 前缀约束（A4 语义不变）。
        var (next, ctx) = await InvokeAs(UserRole.ReadOnly, "GET", "/api/auth/users");
        Assert.False(next);
        Assert.Equal(403, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task OperatorAndAdmin_WriteMethods_NotBlockedByReadOnlyRule()
    {
        // 回归：ReadOnly 拦截不误伤 Operator/Admin（Operator 在业务端点另有权限校验，此处中间件放行）。
        foreach (var role in new[] { UserRole.Operator, UserRole.Admin })
        {
            var (next, _) = await InvokeAs(role, "POST", "/api/waf/rules");
            Assert.True(next, $"{role} POST /api/waf/rules");
        }
    }

    // ---- A13：权限矩阵静态可查 ----

    [Fact]
    public void Admin_HasAllPermissions()
    {
        foreach (var p in Permission.All)
            Assert.True(PermissionMatrix.Has(UserRole.Admin, p), p);
    }

    [Fact]
    public void Operator_ExcludesUserManagementAndAudit()
    {
        Assert.False(PermissionMatrix.Has(UserRole.Operator, Permission.UsersManage));
        Assert.False(PermissionMatrix.Has(UserRole.Operator, Permission.AuditExport));
        Assert.False(PermissionMatrix.Has(UserRole.Operator, Permission.AuditVerify));
        Assert.True(PermissionMatrix.Has(UserRole.Operator, Permission.NodesRead));
        Assert.True(PermissionMatrix.Has(UserRole.Operator, Permission.NodesCommand));
        Assert.True(PermissionMatrix.Has(UserRole.Operator, Permission.SecRead));
        Assert.True(PermissionMatrix.Has(UserRole.Operator, Permission.WafManage));
        Assert.True(PermissionMatrix.Has(UserRole.Operator, Permission.FilesWrite));
        Assert.True(PermissionMatrix.Has(UserRole.Operator, Permission.BackupRun));
    }

    [Fact]
    public void ReadOnly_HasOnlyReadPermissions()
    {
        var granted = PermissionMatrix.For(UserRole.ReadOnly);
        Assert.Equal(2, granted.Count);
        Assert.True(PermissionMatrix.Has(UserRole.ReadOnly, Permission.NodesRead));
        Assert.True(PermissionMatrix.Has(UserRole.ReadOnly, Permission.SecRead));
        foreach (var p in Permission.All.Where(p => !granted.Contains(p)))
            Assert.False(PermissionMatrix.Has(UserRole.ReadOnly, p), p);
    }

    [Fact]
    public void UnknownRole_DefaultsDeny()
    {
        // 未知角色（越界枚举）默认拒绝，不抛异常。
        var rogue = (UserRole)999;
        Assert.Empty(PermissionMatrix.For(rogue));
        Assert.False(PermissionMatrix.Has(rogue, Permission.NodesRead));
    }

    // ---- A12：角色字符串解析 ----

    [Fact]
    public void RoleParser_AcceptsThreeRoles_RejectsUnknown()
    {
        Assert.True(RoleParser.TryParse("Admin", out var a) && a == UserRole.Admin);
        Assert.True(RoleParser.TryParse("Operator", out var op) && op == UserRole.Operator);
        Assert.True(RoleParser.TryParse("ReadOnly", out var ro) && ro == UserRole.ReadOnly);
        foreach (var bad in new[] { "admin", "readonly", "", null, "Root" })
            Assert.False(RoleParser.TryParse(bad, out _), $"bad={bad}");
    }
}
