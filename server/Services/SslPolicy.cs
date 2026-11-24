using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A21 SSL 证书操作权限策略（契约 §14）：list/precheck 为只读动作限
/// ReadOnly 及以上；issue/renew/upload/selfsign 为变更动作限 Operator 及
/// 以上；未知操作默认 Admin（默认拒绝）。单一正源，端点与矩阵测试共用。
/// </summary>
public static class SslPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "list" or "precheck" => UserRole.ReadOnly,
        "issue" or "renew" or "upload" or "selfsign" => UserRole.Operator,
        _ => UserRole.Admin,
    };

    private static readonly Dictionary<UserRole, int> Rank = new()
    {
        [UserRole.ReadOnly] = 0,
        [UserRole.Operator] = 1,
        [UserRole.Admin] = 2,
    };

    /// <summary>角色是否满足操作的最低角色要求；未知角色拒绝。</summary>
    public static bool Allows(string op, UserRole role) =>
        Rank.TryGetValue(role, out var rank) && rank >= Rank[RequiredRole(op)];
}

/// <summary>FR-A21 SSL 端点请求体。</summary>
public sealed record SslOpRequest(string? Op, string? Domain, string? Email, string? Webroot,
    string? CertPem, string? KeyPem, int? Days, int TimeoutMs);

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class SslOpArgs
{
    public static IReadOnlyList<string> Build(SslOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Domain)) args.Add($"domain={req.Domain}");
        if (!string.IsNullOrEmpty(req.Email)) args.Add($"email={req.Email}");
        if (!string.IsNullOrEmpty(req.Webroot)) args.Add($"webroot={req.Webroot}");
        if (!string.IsNullOrEmpty(req.CertPem)) args.Add($"cert_pem={req.CertPem}");
        if (!string.IsNullOrEmpty(req.KeyPem)) args.Add($"key_pem={req.KeyPem}");
        if (req.Days is int days) args.Add($"days={days}");
        return args;
    }
}
