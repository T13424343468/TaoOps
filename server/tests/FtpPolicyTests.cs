using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>FTP 权限矩阵（backlog B2 切真）：list/test 只读放行；adduser/deluser/grant/revoke 限 Operator+；未知操作默认 Admin。</summary>
public class FtpPolicyTests
{
    [Theory]
    [InlineData("list", UserRole.ReadOnly, true)]
    [InlineData("test", UserRole.ReadOnly, true)]
    [InlineData("adduser", UserRole.ReadOnly, false)]
    [InlineData("adduser", UserRole.Operator, true)]
    [InlineData("deluser", UserRole.Operator, true)]
    [InlineData("grant", UserRole.Operator, true)]
    [InlineData("revoke", UserRole.Operator, true)]
    [InlineData("revoke", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Operator, false)]
    public void Allows_MapsOpsToRoles(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, FtpPolicy.Allows(op, role));
    }

    [Fact]
    public void Args_FlattensRequestFields()
    {
        var req = new FtpOpRequest("adduser", "web", "pw", "/srv/web", "/srv/web/uploads", "127.0.0.1", 2121, 30_000);
        var args = FtpOpArgs.Build(req);
        Assert.Contains("op=adduser", args);
        Assert.Contains("user=web", args);
        Assert.Contains("password=pw", args);
        Assert.Contains("root=/srv/web", args);
        Assert.Contains("dir=/srv/web/uploads", args);
        Assert.Contains("host=127.0.0.1", args);
        Assert.Contains("port=2121", args);
    }
}
