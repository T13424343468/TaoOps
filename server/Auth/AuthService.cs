using System.Security.Cryptography;
using TaoOps.Server.Audit;

namespace TaoOps.Server.Auth;

/// <summary>登录结果：成功带会话；失败带原因（locked/invalid），供 API 映射 423/401。</summary>
public sealed class LoginResult
{
    public bool Ok { get; init; }
    public bool Locked { get; init; }
    public LoginSession? Session { get; init; }
}

/// <summary>一条锁定记录（FR-C2：锁定记录可查）。</summary>
public sealed class LockoutRecord
{
    public string Username { get; set; } = "";
    public long LockedAtMs { get; set; }
    public long UnlockAtMs { get; set; }
    public int Failures { get; set; }
}

/// <summary>
/// 登录服务：PBKDF2 口令校验 + 失败锁定（阈值可配、白名单 IP、记录可查）。
/// 登录成败均落审计账本（安全事件可追溯）。
/// </summary>
public sealed class AuthService
{
    private readonly UserStore _users;
    private readonly TokenStore _tokens;
    private readonly AuditStore _audit;
    private readonly AuthPolicy _policy;
    private readonly object _lock = new();
    /// <summary>失败计数：用户名 → (时间窗内失败时刻列表)。仅内存，中控重启清零（锁定是临时态）。</summary>
    private readonly Dictionary<string, List<long>> _failures = new();
    private readonly List<LockoutRecord> _lockouts = [];

    public AuthService(UserStore users, TokenStore tokens, AuditStore audit, AuthPolicy? policy = null)
    {
        _users = users;
        _tokens = tokens;
        _audit = audit;
        _policy = policy ?? AuthPolicy.FromEnvironment();
    }

    public AuthPolicy Policy => _policy;

    /// <summary>PBKDF2-SHA256 哈希（版本化格式 pbkdf2-sha256$iter$salt$hash），带 16 字节随机盐。</summary>
    public static string HashPassword(string password)
    {
        const int iterations = 100_000;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        var iterations = int.TryParse(parts[1], out var it) ? it : 0;
        if (iterations <= 0) return false;
        byte[] salt, expect;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expect = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            // 账本被篡改或损坏的哈希串：校验失败即拒登，不向外抛异常。
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, expect.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expect);
    }

    /// <summary>
    /// 登录：白名单 IP 不计失败不锁定；窗口内连续失败达阈值锁定（FR-C2）。
    /// 成败均落审计（Action=login，Result=ok/locked/invalid）。
    /// </summary>
    public LoginResult Login(string username, string password, string ip, long nowMs)
    {
        var whitelisted = _policy.WhitelistIps.Contains(ip);
        lock (_lock)
        {
            if (!whitelisted && IsLocked(username, nowMs))
            {
                Audit(username, ip, "locked", nowMs);
                return new LoginResult { Locked = true };
            }
            var account = _users.Find(username);
            if (account is null || !VerifyPassword(password, account.PasswordHash))
            {
                if (!whitelisted) CountFailure(username, nowMs);
                Audit(username, ip, "invalid", nowMs);
                return new LoginResult { Ok = false };
            }
            _failures.Remove(username);
            var session = _tokens.Issue(account.Username, account.Role, nowMs);
            Audit(username, ip, "ok", nowMs);
            return new LoginResult { Ok = true, Session = session };
        }
    }

    /// <summary>该用户当前是否处于锁定期。</summary>
    public bool IsLocked(string username, long nowMs)
    {
        lock (_lock)
        {
            _lockouts.RemoveAll(l => l.UnlockAtMs <= nowMs);
            return _lockouts.Any(l => l.Username == username);
        }
    }

    /// <summary>锁定记录（最新在前），过期记录顺手清理。</summary>
    public IReadOnlyList<LockoutRecord> Lockouts(long nowMs)
    {
        lock (_lock)
        {
            _lockouts.RemoveAll(l => l.UnlockAtMs <= nowMs);
            return _lockouts.OrderByDescending(l => l.LockedAtMs).ToList();
        }
    }

    private void CountFailure(string username, long nowMs)
    {
        if (!_failures.TryGetValue(username, out var window))
            _failures[username] = window = [];
        window.Add(nowMs);
        window.RemoveAll(ts => ts < nowMs - _policy.FailureWindowMs);
        if (window.Count >= _policy.MaxFailures)
        {
            _lockouts.Add(new LockoutRecord
            {
                Username = username,
                LockedAtMs = nowMs,
                UnlockAtMs = nowMs + _policy.LockoutMs,
                Failures = window.Count,
            });
            _failures.Remove(username);
        }
    }

    private void Audit(string username, string ip, string result, long nowMs) =>
        _audit.Append(new AuditEvent
        {
            Ts = nowMs,
            User = username,
            Role = "",
            Action = "login",
            Target = "/api/auth/login",
            Ip = ip,
            Result = result,
        });
}
