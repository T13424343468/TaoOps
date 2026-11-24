using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A12 网站统计操作权限策略（PRD：清空/导出涉及 UV/来源 IP 等访问者
/// 个人信息，限 Admin；解析触发日志读取限 Operator 及以上；查询只读放行）。
/// 单一正源，端点与矩阵测试共用。
/// </summary>
public static class WebStatsPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "summary" => UserRole.ReadOnly,
        "parse" => UserRole.Operator,
        "clear" or "export" => UserRole.Admin,
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
