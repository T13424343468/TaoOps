using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A17 日志清理权限矩阵（契约 §12）：list=ReadOnly；add/remove/run=
/// Operator；未知操作默认 Admin（默认拒绝）。
/// </summary>
public class LogCleanPolicyTests
{
    [Theory]
    [InlineData("list", UserRole.ReadOnly, true)]
    [InlineData("list", UserRole.Operator, true)]
    [InlineData("list", UserRole.Admin, true)]
    [InlineData("add", UserRole.ReadOnly, false)]
    [InlineData("add", UserRole.Operator, true)]
    [InlineData("add", UserRole.Admin, true)]
    [InlineData("remove", UserRole.ReadOnly, false)]
    [InlineData("remove", UserRole.Operator, true)]
    [InlineData("run", UserRole.ReadOnly, false)]
    [InlineData("run", UserRole.Operator, true)]
    [InlineData("run", UserRole.Admin, true)]
    public void Matrix_MatchesContract(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, LogCleanPolicy.Allows(op, role));
    }

    [Fact]
    public void UnknownOp_DefaultsAdmin()
    {
        Assert.Equal(UserRole.Admin, LogCleanPolicy.RequiredRole("nope"));
        Assert.False(LogCleanPolicy.Allows("nope", UserRole.Operator));
        Assert.True(LogCleanPolicy.Allows("nope", UserRole.Admin));
    }

    [Fact]
    public void UnknownRole_Rejected()
    {
        Assert.False(LogCleanPolicy.Allows("list", (UserRole)99));
    }

    [Fact]
    public void Build_Add_MapsThresholds()
    {
        var args = LogCleanOpArgs.Build(new LogCleanOpRequest("add", "/var/log/app", 10, 7, null, 0));
        Assert.Equal(new[] { "op=add", "path=/var/log/app", "max_size_mb=10", "max_age_days=7" }, args);
    }

    [Fact]
    public void Build_RemoveAndRun_MapsId()
    {
        var rm = LogCleanOpArgs.Build(new LogCleanOpRequest("remove", null, null, null, "r1", 0));
        Assert.Equal(new[] { "op=remove", "id=r1" }, rm);
        var runAll = LogCleanOpArgs.Build(new LogCleanOpRequest("run", null, null, null, null, 0));
        Assert.Equal(new[] { "op=run" }, runAll);
        var runOne = LogCleanOpArgs.Build(new LogCleanOpRequest("run", null, null, null, "r2", 0));
        Assert.Equal(new[] { "op=run", "id=r2" }, runOne);
    }
}
