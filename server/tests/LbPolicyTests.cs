using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A18 负载均衡权限策略矩阵（契约 §9）：list/health=ReadOnly+，
/// add/remove/drain/undrain=Operator+，未知操作默认 Admin（默认拒绝），
/// 未知角色拒绝。
/// </summary>
public class LbPolicyTests
{
    [Theory]
    [InlineData("list", UserRole.ReadOnly, true)]
    [InlineData("list", UserRole.Operator, true)]
    [InlineData("list", UserRole.Admin, true)]
    [InlineData("health", UserRole.ReadOnly, true)]
    [InlineData("health", UserRole.Operator, true)]
    [InlineData("health", UserRole.Admin, true)]
    [InlineData("add", UserRole.ReadOnly, false)]
    [InlineData("add", UserRole.Operator, true)]
    [InlineData("add", UserRole.Admin, true)]
    [InlineData("remove", UserRole.ReadOnly, false)]
    [InlineData("remove", UserRole.Operator, true)]
    [InlineData("remove", UserRole.Admin, true)]
    [InlineData("drain", UserRole.ReadOnly, false)]
    [InlineData("drain", UserRole.Operator, true)]
    [InlineData("drain", UserRole.Admin, true)]
    [InlineData("undrain", UserRole.ReadOnly, false)]
    [InlineData("undrain", UserRole.Operator, true)]
    [InlineData("undrain", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Operator, false)]
    public void Allows_MatchesMatrix(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, LbPolicy.Allows(op, role));
    }

    [Fact]
    public void Allows_UnknownRole_Rejects()
    {
        Assert.False(LbPolicy.Allows("add", (UserRole)99));
    }
}
