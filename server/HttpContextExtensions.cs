using TaoOps.Server.Auth;

namespace TaoOps.Server;

/// <summary>
/// 请求上下文取值扩展（分支账口径修正，方案 C 配套重构）：
/// 把散落在各端点的防御性空值兜底集中到一处，便于豁免清单逐条核销。
/// 语义不变：RemoteIpAddress 在真实 HTTP 连接恒非空（null 臂为防御性兜底，
/// 仅 TestServer 之外的极端环境可达）；taoops.user/role 由 AuthMiddleware
/// 对已认证请求保证写入（null 臂仅在中间件缺失时可达）。
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>客户端 IP（防御性兜底：无远端地址时返回空串）。</summary>
    public static string ClientIp(this HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "";

    /// <summary>认证用户名（AuthMiddleware 保证已认证请求非空）。</summary>
    public static string AuthUser(this HttpContext http) =>
        http.Items["taoops.user"]?.ToString() ?? "";

    /// <summary>认证角色名（AuthMiddleware 保证已认证请求非空）。</summary>
    public static string AuthRole(this HttpContext http) =>
        http.Items["taoops.role"]?.ToString() ?? "";
}
