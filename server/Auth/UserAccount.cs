using System.Text.Json.Serialization;

namespace TaoOps.Server.Auth;

/// <summary>
/// 面板用户角色：Admin 管理员（用户管理/审计导出）、Operator 操作员（日常运维操作）、
/// ReadOnly 只读观察员（仅查看，写方法一律 403，A12）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole
{
    Admin,
    Operator,
    ReadOnly,
}

/// <summary>
/// 面板用户账号。口令只存 PBKDF2 哈希（A5），任何路径不落明文——
/// 安全帽硬检查点：账本文件、日志、API 响应均无明文口令字段。
/// </summary>
public sealed class UserAccount
{
    public string Username { get; set; } = "";
    /// <summary>版本化哈希格式：pbkdf2-sha256$iterations$saltB64$hashB64。</summary>
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Operator;
    public long CreatedAtMs { get; set; }
}

/// <summary>
/// 登录安全设置（FR-C2 面板侧口径：阈值可配、锁定记录可查、支持白名单 IP）。
/// 旋钮集中一处（编码规范一.5），经环境变量可覆盖，默认值安全从严。
/// </summary>
public sealed class AuthPolicy
{
    /// <summary>窗口内允许的最大连续失败次数，达到即锁定。</summary>
    public int MaxFailures { get; set; } = 5;
    /// <summary>失败计数窗口（毫秒）。</summary>
    public long FailureWindowMs { get; set; } = 15 * 60 * 1000;
    /// <summary>锁定时长（毫秒）。</summary>
    public long LockoutMs { get; set; } = 15 * 60 * 1000;
    /// <summary>白名单 IP（逗号分隔环境变量 TAOOPS_AUTH_WHITELIST）：命中则不计失败、不锁定。</summary>
    public List<string> WhitelistIps { get; set; } = [];

    public static AuthPolicy FromEnvironment() => new()
    {
        MaxFailures = int.TryParse(Environment.GetEnvironmentVariable("TAOOPS_AUTH_MAX_FAILURES"), out var mf) && mf > 0 ? mf : 5,
        FailureWindowMs = long.TryParse(Environment.GetEnvironmentVariable("TAOOPS_AUTH_WINDOW_MS"), out var w) && w > 0 ? w : 15 * 60 * 1000,
        LockoutMs = long.TryParse(Environment.GetEnvironmentVariable("TAOOPS_AUTH_LOCKOUT_MS"), out var l) && l > 0 ? l : 15 * 60 * 1000,
        WhitelistIps = (Environment.GetEnvironmentVariable("TAOOPS_AUTH_WHITELIST") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
    };
}
