using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>环境变量权限矩阵（backlog B2 切真）：detect 只读放行；import/install 限 Operator+；未知操作默认 Admin。</summary>
public class EnvPolicyTests
{
    [Theory]
    [InlineData("detect", UserRole.ReadOnly, true)]
    [InlineData("import", UserRole.ReadOnly, false)]
    [InlineData("import", UserRole.Operator, true)]
    [InlineData("install", UserRole.Operator, true)]
    [InlineData("install", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Operator, false)]
    public void Allows_MapsOpsToRoles(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, EnvPolicy.Allows(op, role));
    }

    [Fact]
    public void Args_FlattensRequestFields()
    {
        var req = new EnvOpRequest("import", "nginx", "/tmp/nginx-offline.tar.gz", 60_000);
        var args = EnvOpArgs.Build(req);
        Assert.Contains("op=import", args);
        Assert.Contains("env=nginx", args);
        Assert.Contains("src=/tmp/nginx-offline.tar.gz", args);
    }
}
