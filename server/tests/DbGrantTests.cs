using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A27 数据库外访权限矩阵：db_grant_list=ReadOnly+；db_grant_add/
/// db_grant_revoke 涉及账号与授权变更限 Admin；未知操作默认拒绝。
/// </summary>
public class DbGrantPolicyTests
{
    [Theory]
    [InlineData("db_grant_list", UserRole.ReadOnly, true)]
    [InlineData("db_grant_list", UserRole.Operator, true)]
    [InlineData("db_grant_add", UserRole.Operator, false)]
    [InlineData("db_grant_add", UserRole.Admin, true)]
    [InlineData("db_grant_revoke", UserRole.Operator, false)]
    [InlineData("db_grant_revoke", UserRole.Admin, true)]
    public void Matrix_MatchesContract(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, DbPolicy.Allows(op, role));
    }

    [Fact]
    public void UnknownOp_DefaultsAdmin()
    {
        Assert.Equal(UserRole.Admin, DbPolicy.RequiredRole("bogus_op"));
        Assert.False(DbPolicy.Allows("bogus_op", UserRole.Operator));
    }

    [Fact]
    public void Args_FlattensRequestFields()
    {
        var req = new DbGrantRequest("db_grant_add", "app_db", "web_read", "10.0.0.%",
            new List<string> { "SELECT", "INSERT" }, null, 30_000);
        var args = DbGrantArgs.Build(req);
        Assert.Contains("op=db_grant_add", args);
        Assert.Contains("database=app_db", args);
        Assert.Contains("user=web_read", args);
        Assert.Contains("host=10.0.0.%", args);
        Assert.Contains("privileges=SELECT,INSERT", args);
        Assert.Equal(5, args.Count);
    }

    [Fact]
    public void Args_RevokeCarriesConfirm()
    {
        var req = new DbGrantRequest("db_grant_revoke", "app_db", "u", "h", null, true, 30_000);
        var args = DbGrantArgs.Build(req);
        Assert.Contains("confirm=true", args);
    }
}
