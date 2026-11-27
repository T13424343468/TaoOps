using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A31 Windows 版平台测试：数据目录三段式解析、前端自带托管（静态文件 + SPA 回退）。
/// 动环境变量的用例并入 Integration 串行集合（既有约定，避免竞态）。
/// </summary>
[Collection("Integration")]
public class WindowsPlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "taoops-win-" + Guid.NewGuid().ToString("N"));
    private string WebDist => Path.Combine(_root, "web");
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "taoops-data-" + Guid.NewGuid().ToString("N"));

    public WindowsPlatformTests()
    {
        Directory.CreateDirectory(WebDist);
        Directory.CreateDirectory(Path.Combine(WebDist, "assets"));
        File.WriteAllText(Path.Combine(WebDist, "index.html"), "<html><body>TaoOps SPA</body></html>");
        File.WriteAllText(Path.Combine(WebDist, "assets", "app.js"), "console.log('taoops');");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
        Environment.SetEnvironmentVariable("TAOOPS_WEB_DIST", null);
    }

    // ---- 数据目录解析（纯函数双向）----

    [Theory]
    [InlineData(true, "C:\\ProgramData")]
    [InlineData(false, "/etc/taoops-pd")]
    public void ResolveDataDir_Defaults_PerPlatform(bool isWindows, string programData)
    {
        var expected = isWindows
            ? Path.Combine(programData, "TaoOps", "data")
            : "/var/lib/taoops/server";
        Assert.Equal(expected, TaoOpsPaths.ResolveDataDir(null, isWindows, programData));
    }

    [Fact]
    public void ResolveDataDir_EnvOverride_WinsOnBothPlatforms()
    {
        Assert.Equal("/custom/dir", TaoOpsPaths.ResolveDataDir("/custom/dir", isWindows: false, programData: "pd"));
        Assert.Equal("D:\\custom", TaoOpsPaths.ResolveDataDir("D:\\custom", isWindows: true, programData: "pd"));
    }

    // ---- 前端目录解析 ----

    [Fact]
    public void ResolveWebDist_EnvExisting_Wins()
    {
        Assert.Equal(WebDist, TaoOpsPaths.ResolveWebDist(WebDist, baseDirectory: "/nonexistent"));
    }

    [Fact]
    public void ResolveWebDist_EnvMissing_FallsBackToBundled()
    {
        // 约定：界面文件在程序目录下 web/ 子目录。
        var baseDir = Path.GetDirectoryName(WebDist)!;
        Assert.Equal(WebDist, TaoOpsPaths.ResolveWebDist(null, baseDir));
    }

    [Fact]
    public void ResolveWebDist_NothingAvailable_ReturnsNull()
    {
        Assert.Null(TaoOpsPaths.ResolveWebDist(null, baseDirectory: "/nonexistent"));
        Assert.Null(TaoOpsPaths.ResolveWebDist("/nonexistent", baseDirectory: "/nonexistent"));
    }

    // ---- 静态托管端到端（独立 factory，走真实 Program 装配）----

    private sealed class WebDistFactory : WebApplicationFactory<Program>
    {
        public WebDistFactory(string dataDir, string webDist)
        {
            Environment.SetEnvironmentVariable("TAOOPS_INSECURE", "1");
            Environment.SetEnvironmentVariable("TAOOPS_DATA_DIR", dataDir);
            Environment.SetEnvironmentVariable("TAOOPS_WEB_DIST", webDist);
        }
    }

    [Fact]
    public async Task StaticHosting_RootServesIndex_AndSpaFallbackWorks()
    {
        using var factory = new WebDistFactory(_dataDir, WebDist);
        using var client = factory.CreateClient();

        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("TaoOps SPA", await root.Content.ReadAsStringAsync());

        // SPA 回退：前端路由直达也返回 index.html。
        var spa = await client.GetAsync("/nodes");
        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
        Assert.Contains("TaoOps SPA", await spa.Content.ReadAsStringAsync());

        // 静态资源按路径命中。
        var js = await client.GetAsync("/assets/app.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Contains("taoops", await js.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StaticHosting_ApiUnauthenticated_Still401_NotFallback()
    {
        using var factory = new WebDistFactory(_dataDir, WebDist);
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/api/auth/users");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task StaticHosting_LoginEndpoint_StillReachable()
    {
        using var factory = new WebDistFactory(_dataDir, WebDist);
        using var client = factory.CreateClient();

        var res = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "nobody", password = "wrong" });
        Assert.NotEqual(HttpStatusCode.NotFound, res.StatusCode);
    }
}
