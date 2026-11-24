using TaoOps.Server.Auth;

namespace TaoOps.Server.Services.K8s;

/// <summary>
/// FR-A28 K8s 集成权限策略：集群管理（增删）=Admin；查看
/// （nodes/workloads/pods/events/logs/version）=ReadOnly 及以上；
/// 资源操作（scale/restart/deploy/remove）=Admin。未知操作默认 Admin
/// （默认拒绝）。单一正源，端点与矩阵测试共用。
/// </summary>
public static class K8sPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "clusters" or "nodes" or "workloads" or "pods" or "events" or "logs" or "version"
            or "rollout_status" or "templates" => UserRole.ReadOnly,
        "scale" or "restart" or "deploy" or "remove" or "add" or "remove_cluster" => UserRole.Admin,
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

/// <summary>FR-A28 K8s 端点请求体（操作 + 参数）。</summary>
public sealed record K8sRequest(
    string? Op,
    string? Name,
    string? Kubeconfig,
    string? Namespace,
    string? Deployment,
    string? Replicas,
    string? Ns,
    string? Pod,
    string? Container,
    string? Template,
    bool Confirm,
    int TimeoutMs);
