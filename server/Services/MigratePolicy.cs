using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A14 一键迁移操作权限策略（PRD：precheck/pack 为源端准备动作限
/// Operator 及以上；restore 覆盖目标路径写操作限 Admin；未知操作默认拒绝）。
/// 单一正源，端点与矩阵测试共用。
/// </summary>
public static class MigratePolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "precheck" or "pack" => UserRole.Operator,
        "restore" => UserRole.Admin,
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
