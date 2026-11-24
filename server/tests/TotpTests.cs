using System.IO;
using TaoOps.Server.Audit;
using TaoOps.Server.Auth;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A16 TOTP：RFC 6238 算法、时钟窗边界、防重放、恢复码哈希与用后即焚、
/// 重新生成旧码失效、密钥落盘权限 600、密码复验。
/// </summary>
public class TotpTests
{
    private static (UserStore Users, AuthService Auth, TokenStore Tokens, AuditStore Audit, TotpStore Store, TotpService Svc, string DataDir) NewEnv()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"totp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var users = new UserStore(dataDir);
        users.Add(new UserAccount
        {
            Username = "alice",
            PasswordHash = AuthService.HashPassword("correct-horse"),
            Role = UserRole.Admin,
            CreatedAtMs = 0,
        });
        var audit = new AuditStore(dataDir);
        // FR-A16 口径：连续 5 次失败锁定 5 分钟。
        var policy = new AuthPolicy { MaxFailures = 5, FailureWindowMs = 15 * 60 * 1000, LockoutMs = 5 * 60 * 1000 };
        var auth = new AuthService(users, new TokenStore(), audit, policy);
        var store = new TotpStore(dataDir);
        var svc = new TotpService(store, users, auth, new TokenStore(), audit);
        return (users, auth, new TokenStore(), audit, store, svc, dataDir);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>用 RFC 6238 同口径算法为测试算出当前 6 位码。</summary>
    private static string ComputeCode(string secret, long step)
    {
        var key = Totp.Base32Decode(secret)!;
        var counter = new byte[8];
        var s = step;
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(s & 0xFF); s >>= 8; }
        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    [Fact]
    public void Base32_RoundTrip()
    {
        var secret = Totp.GenerateSecret();
        Assert.Equal(32, secret.Length); // 20 字节 → 32 个 Base32 字符
        var decoded = Totp.Base32Decode(secret);
        Assert.NotNull(decoded);
        Assert.Equal(20, decoded!.Length);
        Assert.Null(Totp.Base32Decode("1!@#")); // 非法字符
    }

    [Fact]
    public void Verify_CurrentAndWindowBoundaries_Pass()
    {
        var (_, _, _, _, _, svc, _) = NewEnv();
        var secret = Totp.GenerateSecret();
        var now = 1_700_000_000L;
        Assert.True(Totp.Verify(secret, ComputeCode(secret, now / 30), now));            // 当前周期
        Assert.True(Totp.Verify(secret, ComputeCode(secret, now / 30 - 1), now));        // 上一周期（-1 窗口）
        Assert.True(Totp.Verify(secret, ComputeCode(secret, now / 30 + 1), now));        // 下一周期（+1 窗口）
        Assert.False(Totp.Verify(secret, ComputeCode(secret, now / 30 - 2), now));       // 超窗拒绝
        Assert.False(Totp.Verify(secret, ComputeCode(secret, now / 30 + 2), now));
    }

    [Fact]
    public void Verify_RejectsMalformed()
    {
        var secret = Totp.GenerateSecret();
        var now = 1_700_000_000L;
        Assert.False(Totp.Verify(secret, "12345", now));      // 不足 6 位
        Assert.False(Totp.Verify(secret, "1234567", now));    // 超 6 位
        Assert.False(Totp.Verify(secret, "12a456", now));     // 非数字
        Assert.False(Totp.Verify(secret, "", now));
        Assert.False(Totp.Verify("NOT!BASE32", "123456", now)); // 非法密钥
    }

    [Fact]
    public void VerifyLogin_TotpFlow_IssuesSession_AndBlocksReplay()
    {
        var env = NewEnv();
        var (_, auth, _, _, store, svc, _) = env;
        var nowMs = NowMs();
        var (secret, _) = svc.Setup("alice", "correct-horse", "1.2.3.4", nowMs);
        // 绑定确认（Enable）后方可二次校验登录。
        var (enableCodes, enableErr) = svc.Enable("alice", ComputeCode(secret, nowMs / 1000 / 30), "1.2.3.4", nowMs);
        Assert.Equal("", enableErr);
        // Enable 已消耗当前周期，登录码取下一周期（+1 窗口内）。
        var code = ComputeCode(secret, nowMs / 1000 / 30 + 1);

        var r1 = svc.VerifyLogin("alice", code, "1.2.3.4", nowMs);
        Assert.True(r1.Ok);
        Assert.NotNull(r1.Session);
        Assert.Equal("alice", r1.Session!.Username);

        // 同周期重放拒绝（防重放）。
        var r2 = svc.VerifyLogin("alice", code, "1.2.3.4", nowMs);
        Assert.False(r2.Ok);
    }

    [Fact]
    public void VerifyLogin_FiveFailures_LocksFiveMinutes()
    {
        var (_, auth, _, _, _, svc, _) = NewEnv();
        var nowMs = NowMs();
        svc.Setup("alice", "correct-horse", "1.2.3.4", nowMs);
        // 先绑定启用（Enable 消耗当前周期，登录码取下一周期）。
        var account = svc.Store.Load("alice")!;
        var (enableCodes, enableErr) = svc.Enable("alice", ComputeCode(account.Secret, nowMs / 1000 / 30), "1.2.3.4", nowMs);
        Assert.Equal("", enableErr);
        var code = ComputeCode(account.Secret, nowMs / 1000 / 30 + 1);
        Assert.True(svc.VerifyLogin("alice", code, "1.2.3.4", nowMs).Ok);

        // 连续 5 次错误码 → 锁定。
        for (var i = 0; i < 5; i++)
        {
            var later = nowMs + (i + 1) * 1000;
            Assert.False(svc.VerifyLogin("alice", "000000", "1.2.3.4", later).Ok);
        }
        Assert.True(auth.IsLocked("alice", nowMs + 6000));
        Assert.False(auth.IsLocked("alice", nowMs + 6000 + 5 * 60 * 1000)); // 5 分钟后解锁
    }

    [Fact]
    public void RecoveryCodes_HashedOnly_SingleUse_Burn()
    {
        var (_, _, _, _, store, svc, _) = NewEnv();
        var nowMs = NowMs();
        svc.Setup("alice", "correct-horse", "1.2.3.4", nowMs);
        var account = svc.Store.Load("alice")!;
        var code = ComputeCode(account.Secret, nowMs / 1000 / 30);
        var (codes, err) = svc.Enable("alice", code, "1.2.3.4", nowMs);
        Assert.Equal("", err);
        Assert.Equal(10, codes.Length);

        // 落盘只有哈希，无明文。
        var raw = File.ReadAllText(Path.Combine(store.GetType().GetField("_dirPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!.ToString()!,
            "alice.json"));
        Assert.DoesNotContain(codes[0], raw);

        // 第一枚恢复码可用并签发会话。
        var r1 = svc.VerifyLogin("alice", codes[0], "1.2.3.4", NowMs());
        Assert.True(r1.Ok);
        Assert.NotNull(r1.Session);

        // 用后即焚：同一枚第二次失败。
        var r2 = svc.VerifyLogin("alice", codes[0], "1.2.3.4", NowMs());
        Assert.False(r2.Ok);

        // 其余 9 枚仍可用。
        var r3 = svc.VerifyLogin("alice", codes[1], "1.2.3.4", NowMs());
        Assert.True(r3.Ok);
    }

    [Fact]
    public void RegenerateRecovery_InvalidatesOldCodes()
    {
        var (_, _, _, _, _, svc, _) = NewEnv();
        var nowMs = NowMs();
        svc.Setup("alice", "correct-horse", "1.2.3.4", nowMs);
        var account = svc.Store.Load("alice")!;
        var (first, err) = svc.Enable("alice", ComputeCode(account.Secret, nowMs / 1000 / 30), "1.2.3.4", nowMs);
        Assert.Equal("", err);

        var (second, err2) = svc.RegenerateRecovery("alice", "correct-horse", "1.2.3.4", NowMs());
        Assert.Equal("", err2);
        Assert.Equal(10, second.Length);

        // 旧码全部失效：落盘哈希中无任何旧码（避免连续失败触发锁定干扰后续校验）。
        var after = svc.Store.Load("alice")!;
        Assert.Equal(10, after.RecoveryCodeHashes.Count);
        foreach (var old in first)
            Assert.DoesNotContain(after.RecoveryCodeHashes, h => AuthService.VerifyPassword(old, h));
        // 新码在落盘哈希中，且可成功校验。
        Assert.Contains(after.RecoveryCodeHashes, h => AuthService.VerifyPassword(second[0], h));
        Assert.True(svc.VerifyLogin("alice", second[0], "1.2.3.4", NowMs()).Ok);
    }

    [Fact]
    public void Setup_RequiresPasswordReverification()
    {
        var (_, _, _, _, _, svc, _) = NewEnv();
        Assert.Throws<UnauthorizedAccessException>(
            () => svc.Setup("alice", "wrong-password", "1.2.3.4", NowMs()));
        // RegenerateRecovery 以错误元组返回复验失败，不抛异常。
        var (_, regenErr) = svc.RegenerateRecovery("alice", "wrong-password", "1.2.3.4", NowMs());
        Assert.Equal("password re-verification failed", regenErr);
    }

    [Fact]
    public void Store_FilePermissions_600()
    {
        var (_, _, _, _, _, svc, dataDir) = NewEnv();
        svc.Setup("alice", "correct-horse", "1.2.3.4", NowMs());
        var path = Path.Combine(dataDir, "auth", "totp", "alice.json");
        Assert.True(File.Exists(path));
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode & ~UnixFileMode.None);
    }

    [Fact]
    public void Enable_WithoutSetup_Fails()
    {
        var (_, _, _, _, _, svc, _) = NewEnv();
        var (_, err) = svc.Enable("alice", "123456", "1.2.3.4", NowMs());
        Assert.NotEqual("", err);
    }

    [Fact]
    public void VerifyLogin_NotEnabled_Fails()
    {
        var (_, _, _, _, _, svc, _) = NewEnv();
        Assert.False(svc.VerifyLogin("alice", "123456", "1.2.3.4", NowMs()).Ok);
        Assert.False(svc.VerifyLogin("ghost", "123456", "1.2.3.4", NowMs()).Ok);
    }
}
