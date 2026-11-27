using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// 计划任务操作权限策略（backlog B2 切真）：查看任务与执行日志为只读；
/// 新增/删除/立即执行属变更动作，限 Operator 及以上。单一正源，端点与矩阵测试共用。
/// </summary>
public static class CronPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "list" or "logs" => UserRole.ReadOnly,
        "add" or "remove" or "run" => UserRole.Operator,
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
