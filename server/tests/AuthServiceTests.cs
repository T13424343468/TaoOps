using TaoOps.Server.Audit;
using TaoOps.Server.Auth;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/breakpoint-m3-security.md A2/A5（登录锁定与口令存储）。</summary>
public class AuthServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taoops-auth-" + Guid.NewGuid().ToString("N"));
    private UserStore Users { get; }
    private AuditStore Audit { get; }
    private TokenStore Tokens { get; }
    private AuthService Auth { get; }

    private const long StartMs = 1_700_000_000_000;

    public AuthServiceTests()
    {
        Directory.CreateDirectory(_dir);
        Users = new UserStore(_dir);
        Audit = new AuditStore(_dir);
        Tokens = new TokenStore();
        // 阈值 3 次/15 分钟窗，锁定 15 分钟（旋钮显式注入，测试确定性）。
        Auth = new AuthService(Users, Tokens, Audit, new AuthPolicy
        {
            MaxFailures = 3,
            FailureWindowMs = 15 * 60 * 1000,
            LockoutMs = 15 * 60 * 1000,
            WhitelistIps = ["198.51.100.7"],
        });
        Users.Add(new UserAccount
        {
            Username = "admin",
            PasswordHash = AuthService.HashPassword("s3cret-pass"),
            Role = UserRole.Admin,
            CreatedAtMs = StartMs,
        });
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void HashPassword_StoresOnlyPbkdf2_NoPlaintext()
    {
        // A5：账本与哈希串中无明文口令。
        var hash = AuthService.HashPassword("s3cret-pass");
        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.DoesNotContain("s3cret-pass", hash);
        Assert.True(AuthService.VerifyPassword("s3cret-pass", hash));
        Assert.False(AuthService.VerifyPassword("wrong", hash));
        var stored = File.ReadAllText(Users.FilePath);
        Assert.DoesNotContain("s3cret-pass", stored);
    }

    [Fact]
    public void Login_CorrectPassword_IssuesValidToken()
    {
        var r = Auth.Login("admin", "s3cret-pass", "10.0.0.1", StartMs);
        Assert.True(r.Ok);
        Assert.NotNull(Tokens.Validate(r.Session!.Token, StartMs + 1000));
    }

    [Fact]
    public void Login_FailuresReachThreshold_LocksEvenCorrectPassword()
    {
        // A2：连续失败达阈值（3）后锁定，正确口令也被拒。
        for (var i = 0; i < 3; i++)
        {
            var bad = Auth.Login("admin", "wrong", "10.0.0.1", StartMs + i);
            Assert.False(bad.Locked);
        }
        var r = Auth.Login("admin", "s3cret-pass", "10.0.0.1", StartMs + 10_000);
        Assert.True(r.Locked);
        Assert.Null(r.Session);
    }

    [Fact]
    public void Lockout_Queryable_WithRecord()
    {
        // A2：锁定记录可查。
        for (var i = 0; i < 3; i++)
            Auth.Login("admin", "wrong", "10.0.0.1", StartMs + i);
        var lockouts = Auth.Lockouts(StartMs + 10_000);
        var record = Assert.Single(lockouts);
        Assert.Equal("admin", record.Username);
        Assert.Equal(3, record.Failures);
    }

    [Fact]
    public void WhitelistIp_NeverLocks()
    {
        // A2：白名单 IP 不触发锁定。
        for (var i = 0; i < 5; i++)
            Auth.Login("admin", "wrong", "198.51.100.7", StartMs + i);
        var r = Auth.Login("admin", "s3cret-pass", "198.51.100.7", StartMs + 10_000);
        Assert.True(r.Ok);
        Assert.False(r.Locked);
    }

    [Fact]
    public void Lockout_ExpiresAfterWindow()
    {
        for (var i = 0; i < 3; i++)
            Auth.Login("admin", "wrong", "10.0.0.1", StartMs + i);
        // 锁定在第三次失败（StartMs+2）触发，解锁点 = StartMs+2+15min。
        Assert.True(Auth.IsLocked("admin", StartMs + 10_000));
        Assert.False(Auth.IsLocked("admin", StartMs + 15 * 60 * 1000 + 10_000));
    }

    [Fact]
    public void Login_AttemptsAudited()
    {
        // 登录成败均落审计（安全事件可追溯）。
        Auth.Login("admin", "wrong", "10.0.0.1", StartMs);
        Auth.Login("admin", "s3cret-pass", "10.0.0.1", StartMs + 1);
        var events = Audit.Query();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("login", e.Action));
    }

    [Fact]
    public void TokenStore_ExpiredTokenRejected()
    {
        var session = Tokens.Issue("admin", UserRole.Admin, StartMs);
        Assert.NotNull(Tokens.Validate(session.Token, StartMs + 1));
        var expired = Tokens.Issue("admin", UserRole.Admin, StartMs);
        // TokenStore 默认 TTL 8h；直接构造过期场景：校验时刻远晚于签发。
        Assert.Null(Tokens.Validate(expired.Token, StartMs + 8 * 3600 * 1000 + 1));
    }
}
