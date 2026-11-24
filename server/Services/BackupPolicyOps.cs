using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A26 备份策略权限矩阵：policy_list 为只读动作限 ReadOnly 及以上；
/// policy_run 为手动触发限 Operator 及以上；policy_add/policy_remove 为
/// 策略增删限 Admin；未知操作默认 Admin（默认拒绝）。
/// </summary>
public static class BackupPolicyOps
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "policy_list" => UserRole.ReadOnly,
        "policy_run" => UserRole.Operator,
        "policy_add" or "policy_remove" => UserRole.Admin,
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

/// <summary>FR-A26 备份策略端点请求体。</summary>
public sealed record BackupPolicyRequest(string? Op, string? Id, string? Name, string? Source,
    string? Type, string? Schedule, int? Retain, int TimeoutMs);

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class BackupPolicyArgs
{
    public static IReadOnlyList<string> Build(BackupPolicyRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Id)) args.Add($"id={req.Id}");
        if (!string.IsNullOrEmpty(req.Name)) args.Add($"name={req.Name}");
        if (!string.IsNullOrEmpty(req.Source)) args.Add($"source={req.Source}");
        if (!string.IsNullOrEmpty(req.Type)) args.Add($"type={req.Type}");
        if (!string.IsNullOrEmpty(req.Schedule)) args.Add($"schedule={req.Schedule}");
        if (req.Retain is int r) args.Add($"retain={r}");
        return args;
    }
}
