using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>FR-A12 网站统计操作权限矩阵测试。</summary>
public class WebStatsPolicyTests
{
    [Theory]
    [InlineData("summary")]
    public void ReadOnlyOps_AllowReadOnly(string op)
    {
        Assert.True(WebStatsPolicy.Allows(op, UserRole.ReadOnly));
        Assert.True(WebStatsPolicy.Allows(op, UserRole.Operator));
        Assert.True(WebStatsPolicy.Allows(op, UserRole.Admin));
    }

    [Theory]
    [InlineData("parse")]
    public void OperatorOps_RequireOperatorOrAbove(string op)
    {
        Assert.False(WebStatsPolicy.Allows(op, UserRole.ReadOnly));
        Assert.True(WebStatsPolicy.Allows(op, UserRole.Operator));
        Assert.True(WebStatsPolicy.Allows(op, UserRole.Admin));
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("export")]
    [InlineData("unknown_op")]
    public void AdminOps_RequireAdmin(string op)
    {
        Assert.False(WebStatsPolicy.Allows(op, UserRole.ReadOnly));
        Assert.False(WebStatsPolicy.Allows(op, UserRole.Operator));
        Assert.True(WebStatsPolicy.Allows(op, UserRole.Admin));
    }
}
