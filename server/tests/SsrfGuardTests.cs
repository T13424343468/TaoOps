using System.Net;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A20 SSRF 防护矩阵：URL 形态、内网段判定（PRD 清单）、DNS 全记录判定、
/// 白名单（主机名精确 / CIDR）放行。
/// </summary>
public class SsrfGuardTests
{
    [Theory]
    [InlineData("ftp://example.com", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    [InlineData("http://example.com/hook", true)]
    [InlineData("https://open.feishu.cn/hook/x", true)]
    public void ValidateUrl_Shape(string url, bool ok)
    {
        Assert.Equal(ok, SsrfGuard.ValidateUrl(url) is null);
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2606:4700::1111", false)]
    public void IsPrivateIp_MatchesPrdList(string ip, bool expected)
    {
        Assert.Equal(expected, SsrfGuard.IsPrivateIp(IPAddress.Parse(ip)));
    }

    [Fact]
    public async Task ValidateTarget_PrivateResolved_Rejected()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://evil.example/hook", [],
            _ => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") }));
        Assert.NotNull(error);
        Assert.Contains("private", error);
    }

    [Fact]
    public async Task ValidateTarget_PublicResolved_Passes()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://evil.example/hook", [],
            _ => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateTarget_MixedRecords_AnyPrivateRejected()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://evil.example/hook", [],
            _ => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("192.168.0.9") }));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ValidateTarget_WhitelistHostname_Passes()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://internal.example/hook", ["internal.example"],
            _ => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }));
        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateTarget_WhitelistCidr_Passes()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://10.20.0.1/hook", ["10.20.0.0/16"], null);
        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateTarget_WhitelistCidr_OutsideRange_Rejected()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://10.30.0.1/hook", ["10.20.0.0/16"], null);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ValidateTarget_DnsFailure_Rejected()
    {
        var error = await SsrfGuard.ValidateTargetAsync("http://nx.example/hook", [],
            _ => Task.FromException<IPAddress[]>(new Exception("nx")));
        Assert.NotNull(error);
        Assert.Contains("DNS", error);
    }

    [Fact]
    public void InCidr_Ipv4PrefixBoundary()
    {
        Assert.True(SsrfGuard.InCidr(IPAddress.Parse("172.16.0.0"), "172.16.0.0/12"));
        Assert.True(SsrfGuard.InCidr(IPAddress.Parse("172.31.255.255"), "172.16.0.0/12"));
        Assert.False(SsrfGuard.InCidr(IPAddress.Parse("172.32.0.0"), "172.16.0.0/12"));
        Assert.Throws<FormatException>(() => SsrfGuard.InCidr(IPAddress.Parse("1.2.3.4"), "1.2.3.0/33"));
    }
}
