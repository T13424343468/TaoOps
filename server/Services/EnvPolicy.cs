using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// 环境变量（运行环境）操作权限策略（backlog B2 切真）：探测为只读；
/// 离线导入与安装属变更动作限 Operator 及以上。单一正源，端点与矩阵测试共用。
/// </summary>
public static class EnvPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "detect" => UserRole.ReadOnly,
        "import" or "install" => UserRole.Operator,
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

/// <summary>环境变量端点请求体（backlog B2 切真）。</summary>
public sealed record EnvOpRequest(string? Op, string? Env, string? Src, int TimeoutMs);

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class EnvOpArgs
{
    public static IReadOnlyList<string> Build(EnvOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Env)) args.Add($"env={req.Env}");
        if (!string.IsNullOrEmpty(req.Src)) args.Add($"src={req.Src}");
        return args;
    }
}
