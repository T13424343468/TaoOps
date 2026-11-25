using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>主机防火墙权限矩阵（backlog F3）：audit 只读；allow/deny/remove/rollback 限 Operator+；未知操作默认 Admin。</summary>
public class FwPolicyTests
{
    [Theory]
    [InlineData("audit", UserRole.ReadOnly, true)]
    [InlineData("allow", UserRole.ReadOnly, false)]
    [InlineData("allow", UserRole.Operator, true)]
    [InlineData("deny", UserRole.Operator, true)]
    [InlineData("remove", UserRole.Operator, true)]
    [InlineData("rollback", UserRole.Operator, true)]
    [InlineData("rollback", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Operator, false)]
    public void Allows_MapsOpsToRoles(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, FwPolicy.Allows(op, role));
    }
}

/// <summary>防篡改权限矩阵（backlog F3）：status 只读；未知操作默认 Admin。</summary>
public class TamperGuardPolicyTests
{
    [Theory]
    [InlineData("status", UserRole.ReadOnly, true)]
    [InlineData("status", UserRole.Operator, true)]
    [InlineData("unknown_op", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.ReadOnly, false)]
    public void Allows_MapsOpsToRoles(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, TamperGuardPolicy.Allows(op, role));
    }
}
