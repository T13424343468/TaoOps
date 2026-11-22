using TaoOps.Server.Audit;

namespace TaoOps.Server.Audit;

/// <summary>
/// 审计留痕中间件（FR-D4）：所有 /api 非读请求执行后自动落账——
/// 操作人/时间/IP/动作/结果全记录，含 401/403 拒绝事件（匿名探试也留痕）。
/// 登录端点由 AuthService 自行落账（带成败语义），此处跳过防双记。
/// </summary>
public sealed class AuditMiddleware
{
    private readonly RequestDelegate _next;

    public AuditMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, AuditStore store)
    {
        var path = ctx.Request.Path.Value ?? "";
        var isWrite = path.StartsWith("/api/", StringComparison.Ordinal)
            && !HttpMethods.IsGet(ctx.Request.Method);
        if (!isWrite || path == "/api/auth/login")
        {
            await _next(ctx);
            return;
        }

        await _next(ctx);

        store.Append(new AuditEvent
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            User = ctx.Items["taoops.user"] as string ?? "(anonymous)",
            Role = ctx.Items["taoops.role"] as string ?? "",
            Action = ctx.Request.Method,
            Target = path,
            Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
            Result = ctx.Response.StatusCode.ToString(),
        });
    }
}
