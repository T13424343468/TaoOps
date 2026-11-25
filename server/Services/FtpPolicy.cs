using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FTP 操作权限策略（backlog B2 切真）：账号清单与连接测试为只读；
/// 新增/删除账号、目录授权/回收属变更动作限 Operator 及以上。单一正源，端点与矩阵测试共用。
/// </summary>
public static class FtpPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "list" or "test" => UserRole.ReadOnly,
        "adduser" or "deluser" or "grant" or "revoke" => UserRole.Operator,
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

/// <summary>FTP 端点请求体（backlog B2 切真）。</summary>
public sealed record FtpOpRequest(string? Op, string? User, string? Password, string? Root,
    string? Dir, string? Host, int? Port, int TimeoutMs);

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class FtpOpArgs
{
    public static IReadOnlyList<string> Build(FtpOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.User)) args.Add($"user={req.User}");
        if (!string.IsNullOrEmpty(req.Password)) args.Add($"password={req.Password}");
        if (!string.IsNullOrEmpty(req.Root)) args.Add($"root={req.Root}");
        if (!string.IsNullOrEmpty(req.Dir)) args.Add($"dir={req.Dir}");
        if (!string.IsNullOrEmpty(req.Host)) args.Add($"host={req.Host}");
        if (req.Port is int port) args.Add($"port={port}");
        return args;
    }
}
