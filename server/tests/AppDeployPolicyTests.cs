using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>FR-A24 一键部署权限矩阵测试（AppDeployPolicy 单一正源）。</summary>
public class AppDeployPolicyTests
{
    [Theory]
    [InlineData("templates", UserRole.ReadOnly, true)]
    [InlineData("list", UserRole.ReadOnly, true)]
    [InlineData("deploy", UserRole.ReadOnly, false)]
    [InlineData("deploy", UserRole.Operator, false)]
    [InlineData("deploy", UserRole.Admin, true)]
    [InlineData("upgrade", UserRole.Admin, true)]
    [InlineData("upgrade", UserRole.Operator, false)]
    [InlineData("remove", UserRole.Admin, true)]
    [InlineData("remove", UserRole.Operator, false)]
    [InlineData("unknown_op", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Operator, false)]
    public void Allows_MatchesMatrix(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, AppDeployPolicy.Allows(op, role));
    }

    [Fact]
    public void UnknownOp_DefaultsToAdmin()
    {
        Assert.Equal(UserRole.Admin, AppDeployPolicy.RequiredRole("bogus"));
    }

    [Fact]
    public void Args_FlattensEnvOverrides()
    {
        var req = new AppDeployRequest("deploy", "myapp", "mysql",
            new Dictionary<string, string> { ["MYSQL_ROOT_PASSWORD"] = "s3cret" }, 30000);
        var args = AppDeployArgs.Build(req);
        Assert.Contains("op=deploy", args);
        Assert.Contains("app=myapp", args);
        Assert.Contains("template=mysql", args);
        Assert.Contains("env.MYSQL_ROOT_PASSWORD=s3cret", args);
    }
}
