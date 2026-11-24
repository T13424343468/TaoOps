using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A17 日志清理操作权限策略（契约 §12：list 为只读动作限
/// ReadOnly 及以上；add/remove/run 为变更动作限 Operator 及以上；
/// 未知操作默认拒绝）。单一正源，端点与矩阵测试共用。
/// </summary>
public static class LogCleanPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "list" => UserRole.ReadOnly,
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

/// <summary>FR-A17 日志清理端点请求体。</summary>
public sealed record LogCleanOpRequest(string? Op, string? Path, long? MaxSizeMb, int? MaxAgeDays, string? Id, int TimeoutMs);

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class LogCleanOpArgs
{
    public static IReadOnlyList<string> Build(LogCleanOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Path)) args.Add($"path={req.Path}");
        if (req.MaxSizeMb is long size) args.Add($"max_size_mb={size}");
        if (req.MaxAgeDays is int age) args.Add($"max_age_days={age}");
        if (!string.IsNullOrEmpty(req.Id)) args.Add($"id={req.Id}");
        return args;
    }
}
