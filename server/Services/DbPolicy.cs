using TaoOps.Server.Auth;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A15 复制操作权限策略（契约 §13）：replica_status 为只读动作限
/// ReadOnly 及以上；master_grant/replica_config/replica_reset 涉及凭据
/// 与复制拓扑变更限 Admin。backlog B2 切真扩展：backups（备份清单）为只读；
/// create/drop/backup/restore 属库级变更动作限 Operator 及以上。
/// 单一正源，端点与矩阵测试共用。
/// </summary>
public static class DbPolicy
{
    /// <summary>操作 → 最低角色。未知操作默认 Admin（默认拒绝）。</summary>
    public static UserRole RequiredRole(string op) => op switch
    {
        "replica_status" => UserRole.ReadOnly,
        "db_grant_list" => UserRole.ReadOnly,
        "backups" => UserRole.ReadOnly,
        "create" or "drop" or "backup" or "restore" => UserRole.Operator,
        "db_grant_add" or "db_grant_revoke" => UserRole.Admin,
        "master_grant" or "replica_config" or "replica_reset" => UserRole.Admin,
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

/// <summary>FR-A15 复制端点请求体。</summary>
public sealed record DbReplicaRequest(string? Op, string? ReplicaHost, string? MasterHost, string? MasterPort,
    string? MasterUser, string? MasterPassword, string? MasterLogFile, int? MasterLogPos, int TimeoutMs);

/// <summary>FR-A27 数据库外访端点请求体。</summary>
public sealed record DbGrantRequest(string? Op, string? Database, string? User, string? Host,
    IReadOnlyList<string>? Privileges, bool? Confirm, int TimeoutMs);

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class DbGrantArgs
{
    public static IReadOnlyList<string> Build(DbGrantRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Database)) args.Add($"database={req.Database}");
        if (!string.IsNullOrEmpty(req.User)) args.Add($"user={req.User}");
        if (!string.IsNullOrEmpty(req.Host)) args.Add($"host={req.Host}");
        if (req.Privileges is { Count: > 0 }) args.Add($"privileges={string.Join(",", req.Privileges)}");
        if (req.Confirm is bool c) args.Add($"confirm={(c ? "true" : "false")}");
        return args;
    }
}

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class DbReplicaArgs
{
    public static IReadOnlyList<string> Build(DbReplicaRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.ReplicaHost)) args.Add($"replica_host={req.ReplicaHost}");
        if (!string.IsNullOrEmpty(req.MasterHost)) args.Add($"master_host={req.MasterHost}");
        if (!string.IsNullOrEmpty(req.MasterPort)) args.Add($"master_port={req.MasterPort}");
        if (!string.IsNullOrEmpty(req.MasterUser)) args.Add($"master_user={req.MasterUser}");
        if (!string.IsNullOrEmpty(req.MasterPassword)) args.Add($"master_password={req.MasterPassword}");
        if (!string.IsNullOrEmpty(req.MasterLogFile)) args.Add($"master_log_file={req.MasterLogFile}");
        if (req.MasterLogPos is int pos) args.Add($"master_log_pos={pos}");
        return args;
    }
}

/// <summary>数据库基础操作端点请求体（backlog B2 切真）。</summary>
public sealed record DbOpRequest(string? Op, string? Engine, string? Name, string? Dump, int TimeoutMs);

/// <summary>把请求字段摊平成 key=value 参数（Agent 侧 parseKeyValues 约定）。</summary>
internal static class DbOpArgs
{
    public static IReadOnlyList<string> Build(DbOpRequest req)
    {
        var args = new List<string> { $"op={req.Op}" };
        if (!string.IsNullOrEmpty(req.Engine)) args.Add($"engine={req.Engine}");
        if (!string.IsNullOrEmpty(req.Name)) args.Add($"name={req.Name}");
        if (!string.IsNullOrEmpty(req.Dump)) args.Add($"dump={req.Dump}");
        return args;
    }
}
