using TaoOps.Server.Auth;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A25 在线文件编辑权限矩阵：file_read/file_write 均为变更动作限
/// Operator 及以上；未知操作默认拒绝（Admin 才放行）。
/// </summary>
public class FilesPolicyTests
{
    [Theory]
    [InlineData("file_read", UserRole.ReadOnly, false)]
    [InlineData("file_read", UserRole.Operator, true)]
    [InlineData("file_read", UserRole.Admin, true)]
    [InlineData("file_write", UserRole.ReadOnly, false)]
    [InlineData("file_write", UserRole.Operator, true)]
    [InlineData("file_write", UserRole.Admin, true)]
    public void Matrix_MatchesContract(string op, UserRole role, bool expected)
    {
        Assert.Equal(expected, FilesPolicy.Allows(op, role));
    }

    [Fact]
    public void UnknownOp_DefaultsAdmin()
    {
        Assert.Equal(UserRole.Admin, FilesPolicy.RequiredRole("chmod"));
        Assert.False(FilesPolicy.Allows("chmod", UserRole.Operator));
    }

    [Fact]
    public void Args_FlattensRequestFields()
    {
        var req = new FileEditRequest("file_write", "/etc/app/nginx.conf", "k=v", 30_000);
        var args = FileEditArgs.Build(req);
        Assert.Contains("op=file_write", args);
        Assert.Contains("path=/etc/app/nginx.conf", args);
        Assert.Contains("content=k=v", args);
        Assert.Equal(3, args.Count);
    }

    [Fact]
    public void Args_ReadOmitsContent()
    {
        var req = new FileEditRequest("file_read", "/etc/hosts", null, 30_000);
        var args = FileEditArgs.Build(req);
        Assert.Equal(2, args.Count);
        Assert.DoesNotContain(args, a => a.StartsWith("content="));
    }
}
