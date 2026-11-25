using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaoOps.Server.Audit;

namespace TaoOps.Server.Auth;

/// <summary>
/// FR-A16 TOTP 账号配置：密钥、启用态、恢复码哈希（明文不落盘）。
/// </summary>
public sealed class TotpAccount
{
    public string Secret { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>恢复码仅存 PBKDF2 哈希（口径同口令），用后即焚。</summary>
    public List<string> RecoveryCodeHashes { get; set; } = [];
    public long CreatedMs { get; set; }
    /// <summary>已使用的最近 TOTP 步数（防重放：拒绝同周期/更早周期码）。</summary>
    public long LastUsedStep { get; set; } = -1;
}

/// <summary>
/// TOTP 存储：每用户一文件（dataDir/auth/totp/&lt;username&gt;.json），
/// 落盘权限 600（验收口径），tmp+move 原子写盘（家法三.6）。
/// </summary>
public sealed class TotpStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _dirPath;
    private readonly object _lock = new();

    public TotpStore(string dataDir)
    {
        _dirPath = Path.Combine(dataDir, "auth", "totp");
        Directory.CreateDirectory(_dirPath);
        // 目录须保留执行位（700）：600 无 x 位时非 root 进程无法在其中创建文件。
        TryChmod(_dirPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string PathFor(string username) =>
        Path.Combine(_dirPath, Sanitize(username) + ".json");

    private static string Sanitize(string username) =>
        string.Concat(username.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'));

    public TotpAccount? Load(string username)
    {
        lock (_lock)
        {
            var path = PathFor(username);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TotpAccount>(File.ReadAllText(path), JsonOpts);
        }
    }

    public void Save(string username, TotpAccount account)
    {
        lock (_lock)
        {
            var path = PathFor(username);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(account, JsonOpts));
            File.Move(tmp, path, overwrite: true);
            TryChmod600(path);
        }
    }

    public bool Delete(string username)
    {
        lock (_lock)
        {
            var path = PathFor(username);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }

    /// <summary>落盘权限 600（验收口径）；受限平台尽力而为不抛错。</summary>
    private static void TryChmod600(string path) =>
        TryChmod(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    /// <summary>按目标权限位设置 Unix 文件模式；受限平台尽力而为不抛错。</summary>
    private static void TryChmod(string path, UnixFileMode mode)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, mode);
        }
        catch (PlatformNotSupportedException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// TOTP 二次校验结果：成功带会话（经 TokenStore 签发），失败带是否触发锁定。
/// </summary>
public sealed class TotpVerifyResult
{
    public bool Ok { get; init; }
    public bool Locked { get; init; }
    public LoginSession? Session { get; init; }
}

/// <summary>
/// TOTP 业务：绑定（密码复验）、登录二次校验（含恢复码）、重生成。
/// 校验失败计入 AuthService 失败锁定通道（连续 5 次锁 5 分钟）。
/// </summary>
public sealed class TotpService
{
    private readonly TotpStore _store;
    private readonly UserStore _users;
    private readonly AuthService _auth;
    private readonly TokenStore _tokens;
    private readonly AuditStore _audit;

    public TotpService(TotpStore store, UserStore users, AuthService auth, TokenStore tokens, AuditStore audit)
    {
        _store = store;
        _users = users;
        _auth = auth;
        _tokens = tokens;
        _audit = audit;
    }

    public TotpStore Store => _store;

    /// <summary>生成待绑定密钥（不置 Enabled）；返回密钥与 otpauth URI。</summary>
    public (string Secret, string Uri) Setup(string username, string password, string ip, long nowMs)
    {
        RequirePasswordReauth(username, password, ip, nowMs);
        var account = _store.Load(username) ?? new TotpAccount();
        account.Secret = Totp.GenerateSecret();
        account.CreatedMs = nowMs;
        account.Enabled = false;
        _store.Save(username, account);
        Audit(username, ip, "totp.setup", nowMs);
        return (account.Secret, Totp.OtpauthUri(account.Secret, username));
    }

    /// <summary>确认绑定：6 位码校验通过后置 Enabled，返回 10 枚恢复码明文（仅此一次）。</summary>
    public (string[] Codes, string Error) Enable(string username, string code, string ip, long nowMs)
    {
        var account = _store.Load(username);
        if (account is null || account.Secret.Length == 0)
            return ([], "totp not set up");
        var step = Totp.MatchStep(account.Secret, code, nowMs / 1000, account.LastUsedStep);
        if (step < 0)
        {
            Audit(username, ip, "totp.enable", nowMs, "invalid");
            return ([], "invalid totp code");
        }
        account.Enabled = true;
        account.LastUsedStep = step;
        var (plain, hashes) = GenerateRecoveryCodes();
        account.RecoveryCodeHashes = hashes;
        _store.Save(username, account);
        Audit(username, ip, "totp.enable", nowMs);
        return (plain, "");
    }

    /// <summary>
    /// 登录二次校验：6 位 TOTP 或恢复码。失败计入锁定通道（连续 5 次锁 5 分钟）。
    /// 成功签发会话（与密码登录同一 TokenStore）。
    /// </summary>
    public TotpVerifyResult VerifyLogin(string username, string? code, string ip, long nowMs)
    {
        var account = _store.Load(username);
        if (account is null || !account.Enabled)
            return new TotpVerifyResult { Ok = false };

        if (_auth.IsLocked(username, nowMs))
            return new TotpVerifyResult { Locked = true };

        var user = _users.Find(username);
        if (user is null)
            return new TotpVerifyResult { Ok = false };

        // 恢复码：哈希比对，命中即焚（从列表移除并落盘）。
        var recoveryHit = account.RecoveryCodeHashes
            .FirstOrDefault(h => AuthService.VerifyPassword(code ?? "", h));
        if (recoveryHit is not null)
        {
            account.RecoveryCodeHashes.Remove(recoveryHit);
            _store.Save(username, account);
            Audit(username, ip, "totp.verify", nowMs, "ok");
            return new TotpVerifyResult
            {
                Ok = true,
                Session = _tokens.Issue(user.Username, user.Role, nowMs),
            };
        }

        var step = Totp.MatchStep(account.Secret, code ?? "", nowMs / 1000, account.LastUsedStep);
        if (step < 0)
        {
            _auth.RecordFailure(username, nowMs);
            Audit(username, ip, "totp.verify", nowMs, "invalid");
            return new TotpVerifyResult { Ok = false };
        }
        account.LastUsedStep = step;
        _store.Save(username, account);
        Audit(username, ip, "totp.verify", nowMs);
        return new TotpVerifyResult
        {
            Ok = true,
            Session = _tokens.Issue(user.Username, user.Role, nowMs),
        };
    }

    /// <summary>重新生成 10 枚恢复码（需密码复验），旧码全部失效。</summary>
    public (string[] Codes, string Error) RegenerateRecovery(string username, string password, string ip, long nowMs)
    {
        try
        {
            RequirePasswordReauth(username, password, ip, nowMs);
        }
        catch (UnauthorizedAccessException)
        {
            return ([], "password re-verification failed");
        }
        var account = _store.Load(username);
        if (account is null || !account.Enabled)
            return ([], "totp not enabled");
        var (plain, hashes) = GenerateRecoveryCodes();
        account.RecoveryCodeHashes = hashes;
        _store.Save(username, account);
        Audit(username, ip, "totp.recovery.regenerate", nowMs);
        return (plain, "");
    }

    public bool IsEnabled(string username) =>
        _store.Load(username)?.Enabled ?? false;

    private void RequirePasswordReauth(string username, string password, string ip, long nowMs)
    {
        var account = _users.Find(username);
        if (account is null || !AuthService.VerifyPassword(password ?? "", account.PasswordHash))
        {
            Audit(username, ip, "totp.auth", nowMs, "invalid");
            throw new UnauthorizedAccessException("password re-verification failed");
        }
    }

    /// <summary>生成 10 枚恢复码：明文返回一次，存储侧只留哈希。</summary>
    private static (string[] Plain, List<string> Hashes) GenerateRecoveryCodes()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var plain = new string[10];
        var hashes = new List<string>(10);
        for (var i = 0; i < 10; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(8);
            plain[i] = new string(bytes.Select(b => alphabet[b % 32]).ToArray());
            hashes.Add(AuthService.HashPassword(plain[i]));
        }
        return (plain, hashes);
    }

    private void Audit(string username, string ip, string action, long nowMs,
        string result = "ok") =>
        _audit.Append(new AuditEvent
        {
            Ts = nowMs,
            User = username,
            Role = "",
            Action = action,
            Target = "/api/auth/totp",
            Ip = ip,
            Result = result,
        });
}
