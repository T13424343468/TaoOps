using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>计划任务权限矩阵（backlog B2 切真）：list/logs 只读放行；add/remove/run 限 Operator+；未知操作默认 Admin。</summary>
public class CronPolicyTests
{
    [Theory]
    [InlineData("list", UserRole.ReadOnly, true)]
    [InlineData("logs", UserRole.ReadOnly, true)]
    [InlineData("add", UserRole.ReadOnly, false)]
    [InlineData("add", UserRole.Operator, true)]
    [InlineData("remove", UserRole.Operator, true)]
    [InlineData("run", UserRole.Operator, true)]
    [InlineData("run", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Admin, true)]
    [InlineData("unknown_op", UserRole.Operator, false)]
    public void Allows_MapsOpsToRoles(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, CronPolicy.Allows(op, role));
    }
}
