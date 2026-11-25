using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A18 负载均衡操作权限策略（契约 §9：list/health 为只读动作限
/// ReadOnly 及以上；add/remove/drain/undrain 为变更动作限 Operator 及
/// 以上；未知操作默认拒绝）。
/// 单一正源，端点与矩阵测试共用。
/// </summary>
public static class LbPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "list" or "health" => UserRole.ReadOnly,
        "add" or "remove" or "drain" or "undrain" => UserRole.Operator,
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
