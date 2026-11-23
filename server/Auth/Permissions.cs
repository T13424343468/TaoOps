namespace TaoOps.Server.Auth;

/// <summary>
/// 权限点常量（A13）：面板内最小可授权单元，命名按「资源.动作」。
/// 端点接线收口时由 Program.cs 按 Permission 点校验；中间件先按角色兜底。
/// </summary>
public static class Permission
{
    public const string UsersManage = "users.manage";
    public const string AuditExport = "audit.export";
    public const string AuditVerify = "audit.verify";
    public const string NodesRead = "nodes.read";
    public const string NodesCommand = "nodes.command";
    public const string SecRead = "sec.read";
    public const string WafManage = "waf.manage";
    public const string FilesWrite = "files.write";
    public const string BackupRun = "backup.run";

    /// <summary>全部权限点，供 /api/auth/permissions 展示与矩阵测试遍历。</summary>
    public static readonly IReadOnlyList<string> All =
    [
        UsersManage, AuditExport, AuditVerify,
        NodesRead, NodesCommand,
        SecRead,
        WafManage, FilesWrite, BackupRun,
    ];
}

/// <summary>
/// 角色 → 权限矩阵（A13）：静态可查、单一正源。
/// Admin 全量；Operator 日常运维（无用户管理与审计导出/校验）；
/// ReadOnly 仅查看（nodes.read / sec.read）。
/// </summary>
public static class PermissionMatrix
{
    private static readonly Dictionary<UserRole, IReadOnlySet<string>> Matrix = new()
    {
        [UserRole.Admin] = new HashSet<string>(Permission.All),
        [UserRole.Operator] = new HashSet<string>
        {
            Permission.NodesRead, Permission.NodesCommand,
            Permission.SecRead,
            Permission.WafManage, Permission.FilesWrite, Permission.BackupRun,
        },
        [UserRole.ReadOnly] = new HashSet<string>
        {
            Permission.NodesRead, Permission.SecRead,
        },
    };

    /// <summary>查询角色拥有的权限集合；未知角色返回空集（默认拒绝）。</summary>
    public static IReadOnlySet<string> For(UserRole role) =>
        Matrix.TryGetValue(role, out var set) ? set : new HashSet<string>();

    /// <summary>角色是否持有指定权限点。</summary>
    public static bool Has(UserRole role, string permission) => For(role).Contains(permission);
}

/// <summary>角色字符串解析（A12）：创建用户接受 "Admin"/"Operator"/"ReadOnly"，未知值拒绝而非静默降级。</summary>
public static class RoleParser
{
    public static bool TryParse(string? value, out UserRole role)
    {
        switch (value)
        {
            case nameof(UserRole.Admin): role = UserRole.Admin; return true;
            case nameof(UserRole.Operator): role = UserRole.Operator; return true;
            case nameof(UserRole.ReadOnly): role = UserRole.ReadOnly; return true;
            default: role = UserRole.Operator; return false;
        }
    }
}
