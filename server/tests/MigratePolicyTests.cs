using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A14 一键迁移权限策略矩阵：precheck/pack=Operator+，restore=Admin，
/// 未知操作默认 Admin（默认拒绝），未知角色拒绝。
/// </summary>
public class MigratePolicyTests
{
    [Theory]
    [InlineData("precheck", UserRole.ReadOnly, false)]
    [InlineData("precheck", UserRole.Operator, true)]
    [InlineData("precheck", UserRole.Admin, true)]
    [InlineData("pack", UserRole.ReadOnly, false)]
    [InlineData("pack", UserRole.Operator, true)]
    [InlineData("pack", UserRole.Admin, true)]
    [InlineData("restore", UserRole.ReadOnly, false)]
    [InlineData("restore", UserRole.Operator, false)]
    [InlineData("restore", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Operator, false)]
    public void Allows_MatchesMatrix(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, MigratePolicy.Allows(op, role));
    }

    [Fact]
    public void Allows_UnknownRole_Rejects()
    {
        Assert.False(MigratePolicy.Allows("precheck", (UserRole)99));
    }
}
