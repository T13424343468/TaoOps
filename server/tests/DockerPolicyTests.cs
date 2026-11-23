using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>FR-A11 Docker 操作权限策略测试（PRD：终端/删镜像限 Admin，启停限 Operator+，只读操作放行 ReadOnly）。</summary>
public class DockerPolicyTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("logs")]
    [InlineData("image_list")]
    public void ReadOps_AllowReadOnly(string op)
    {
        Assert.True(DockerPolicy.Allows(op, UserRole.ReadOnly));
        Assert.True(DockerPolicy.Allows(op, UserRole.Operator));
        Assert.True(DockerPolicy.Allows(op, UserRole.Admin));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("image_pull")]
    public void LifecycleOps_RequireOperatorPlus(string op)
    {
        Assert.False(DockerPolicy.Allows(op, UserRole.ReadOnly));
        Assert.True(DockerPolicy.Allows(op, UserRole.Operator));
        Assert.True(DockerPolicy.Allows(op, UserRole.Admin));
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("image_remove")]
    public void AdminOps_RequireAdmin(string op)
    {
        Assert.False(DockerPolicy.Allows(op, UserRole.ReadOnly));
        Assert.False(DockerPolicy.Allows(op, UserRole.Operator));
        Assert.True(DockerPolicy.Allows(op, UserRole.Admin));
    }

    [Fact]
    public void UnknownOp_DefaultsToAdmin()
    {
        Assert.Equal(UserRole.Admin, DockerPolicy.RequiredRole("nope"));
        Assert.False(DockerPolicy.Allows("nope", UserRole.Operator));
    }
}
