using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A24 应用一键部署权限策略：模板与已部署应用列表只读放行；
/// 部署/升级/删除整应用属高影响变更，限 Admin。未知操作默认 Admin
/// （默认拒绝）。单一正源，端点与矩阵测试共用。
/// </summary>
public static class AppDeployPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "templates" or "list" => UserRole.ReadOnly,
        "deploy" or "upgrade" or "remove" => UserRole.Admin,
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

/// <summary>FR-A24 一键部署端点请求体。</summary>
public sealed record AppDeployRequest(string? Op, string? App, string? Template, Dictionary<string, string>? Env, int TimeoutMs);

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class AppDeployArgs
{
    public static IReadOnlyList<string> Build(AppDeployRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.App)) args.Add($"app={req.App}");
        if (!string.IsNullOrEmpty(req.Template)) args.Add($"template={req.Template}");
        if (req.Env is not null)
        {
            foreach (var kv in req.Env)
                args.Add($"env.{kv.Key}={kv.Value}");
        }
        return args;
    }
}
