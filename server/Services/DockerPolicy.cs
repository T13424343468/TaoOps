using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A11 Docker 操作权限策略（PRD：容器终端/删除镜像限 Admin；
/// 启停限 Operator 及以上；列表/日志类只读操作放行 ReadOnly）。
/// 单一正源，端点与矩阵测试共用。
/// </summary>
public static class DockerPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "list" or "logs" or "image_list" => UserRole.ReadOnly,
        "start" or "stop" or "restart" or "image_pull" => UserRole.Operator,
        "terminal" or "image_remove" => UserRole.Admin,
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
