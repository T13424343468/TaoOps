using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>网站权限矩阵（backlog B2 切真）：list/backups 只读放行；create/update/delete 限 Operator+；未知操作默认 Admin。</summary>
public class SitePolicyTests
{
    [Theory]
    [InlineData("list", UserRole.ReadOnly, true)]
    [InlineData("backups", UserRole.ReadOnly, true)]
    [InlineData("create", UserRole.ReadOnly, false)]
    [InlineData("create", UserRole.Operator, true)]
    [InlineData("update", UserRole.Operator, true)]
    [InlineData("delete", UserRole.Operator, true)]
    [InlineData("delete", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Operator, false)]
    public void Allows_MapsOpsToRoles(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, SitePolicy.Allows(op, role));
    }
}
