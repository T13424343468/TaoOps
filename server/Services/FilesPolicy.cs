using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A25 在线文件编辑权限策略：file_read/file_write 均为变更动作
/// （配置文件读写属敏感操作）限 Operator 及以上；未知操作默认 Admin
/// （默认拒绝）。单一正源，端点与矩阵测试共用。
/// </summary>
public static class FilesPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "file_read" or "file_write" => UserRole.Operator,
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

/// <summary>FR-A25 文件编辑端点请求体。</summary>
public sealed record FileEditRequest(string? Op, string? Path, string? Content, int TimeoutMs);

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class FileEditArgs
{
    public static IReadOnlyList<string> Build(FileEditRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Path)) args.Add($"path={req.Path}");
        if (req.Content is not null) args.Add($"content={req.Content}");
        return args;
    }
}
