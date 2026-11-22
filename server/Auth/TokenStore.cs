using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TaoOps.Server.Auth;

/// <summary>登录会话：不透明随机 token → 用户名/角色，带过期时间。</summary>
public sealed class LoginSession
{
    public string Token { get; init; } = "";
    public string Username { get; init; } = "";
    public UserRole Role { get; init; }
    public long ExpiresAtMs { get; init; }
}

/// <summary>
/// 会话账本（中控内存）：token 签发与校验。中控重启即失效，需重新登录
/// （已知边界，见断点档案）。过期与登出即刻作废。
/// </summary>
public sealed class TokenStore
{
    /// <summary>会话有效期（毫秒），TAOOPS_SESSION_HOURS 可覆盖，默认 8 小时。</summary>
    private readonly long _ttlMs;
    private readonly ConcurrentDictionary<string, LoginSession> _sessions = new();

    public TokenStore()
    {
        _ttlMs = long.TryParse(Environment.GetEnvironmentVariable("TAOOPS_SESSION_MS"), out var ms) && ms > 0
            ? ms
            : (long.TryParse(Environment.GetEnvironmentVariable("TAOOPS_SESSION_HOURS"), out var h) && h > 0 ? h : 8) * 3600 * 1000;
    }

    /// <summary>签发新会话 token（CSPRNG 32 字节，base64url）。</summary>
    public LoginSession Issue(string username, UserRole role, long nowMs)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var session = new LoginSession
        {
            Token = token,
            Username = username,
            Role = role,
            ExpiresAtMs = nowMs + _ttlMs,
        };
        _sessions[token] = session;
        return session;
    }

    /// <summary>校验 token：存在且未过期返回会话，否则 null（过期即摘除）。</summary>
    public LoginSession? Validate(string? token, long nowMs)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (session.ExpiresAtMs <= nowMs)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        return session;
    }

    public bool Revoke(string token) => _sessions.TryRemove(token, out _);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
