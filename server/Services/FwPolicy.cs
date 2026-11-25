using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// 主机防火墙操作权限策略（backlog F3）：查看审计记录为只读；
/// 放行/封禁/删除规则/回滚属变更动作，限 Operator 及以上。单一正源，端点与矩阵测试共用。
/// </summary>
public static class FwPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "audit" => UserRole.ReadOnly,
        "allow" or "deny" or "remove" or "rollback" => UserRole.Operator,
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

/// <summary>
/// 防篡改操作权限策略（backlog F3）：状态查看为只读（当前仅 status）。
/// 单一正源，端点与矩阵测试共用。
/// </summary>
public static class TamperGuardPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "status" => UserRole.ReadOnly,
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
