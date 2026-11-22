using TaoOps.Server.Auth;

namespace TaoOps.Server.Auth;

/// <summary>
/// /api 鉴权中间件：未带有效 token → 401；角色不足 → 403（A4）。
/// 匿名白名单：登录与 bootstrap（bootstrap 仅账号库为空时由端点自身放行）。
/// gRPC 与 /ws 通道不经过本中间件（M2 TLS+TOFU 已覆盖节点侧）。
/// </summary>
public sealed class AuthMiddleware
{
    /// <summary>仅 Admin 可访问的路径前缀（用户管理与审计导出/校验）。</summary>
    private static readonly string[] AdminOnlyPrefixes =
    [
        "/api/auth/users",
        "/api/auth/lockouts",
        "/api/audit/export",
        "/api/audit/verify",
    ];

    private static readonly string[] AnonymousPaths =
    [
        "/api/auth/login",
        "/api/auth/bootstrap",
    ];

    private readonly RequestDelegate _next;

    public AuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, TokenStore tokens)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await _next(ctx);
            return;
        }
        if (AnonymousPaths.Contains(path, StringComparer.Ordinal))
        {
            await _next(ctx);
            return;
        }

        var raw = ctx.Request.Headers.Authorization.ToString();
        var token = raw.StartsWith("Bearer ", StringComparison.Ordinal) ? raw["Bearer ".Length..].Trim() : raw;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = tokens.Validate(token, now);
        if (session is null)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "authentication required" });
            return;
        }
        if (session.Role != UserRole.Admin && AdminOnlyPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal)))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "admin role required" });
            return;
        }
        ctx.Items["taoops.user"] = session.Username;
        ctx.Items["taoops.role"] = session.Role.ToString();
        await _next(ctx);
    }
}
