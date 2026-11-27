using System.Net;
using TaoOps.Server.Audit;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>断言清单正源：docs/prd-v14-b3.md §五 FR-A29 外接模式安全口径。</summary>
public class AiAdviceExternalTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "taoops-aiext-" + Guid.NewGuid().ToString("N"));
    private readonly AuditStore _audit;

    public AiAdviceExternalTests()
    {
        _audit = new AuditStore(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private AiAdviceExternal NewExt(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
        Func<DateTimeOffset>? now = null) =>
        new(_dataDir, _audit, send, now);

    // ---- 配置：URL 校验 + Key 不回显 ----

    [Fact]
    public void SaveConfig_InvalidUrl_Throws()
    {
        var ext = NewExt();
        Assert.Throws<ArgumentException>(() =>
            ext.SaveConfig(new AiAdviceExternal.ExternalConfig("ftp://x", "key", [])));
        Assert.Throws<ArgumentException>(() =>
            ext.SaveConfig(new AiAdviceExternal.ExternalConfig("not a url", "key", [])));
    }

    [Fact]
    public void LoadConfigSafe_NeverReturnsKey()
    {
        var ext = NewExt();
        ext.SaveConfig(new AiAdviceExternal.ExternalConfig("https://api.example.com/v1/chat", "secret-key", ["api.example.com"]));
        var (endpoint, hasKey, whitelist) = ext.LoadConfigSafe();
        Assert.Equal("https://api.example.com/v1/chat", endpoint);
        Assert.True(hasKey);
        Assert.DoesNotContain("secret-key", endpoint); // Key 不回显
        Assert.Contains("api.example.com", whitelist);
    }

    [Fact]
    public void LoadConfigSafe_CorruptedFile_FallsBackToDefaults()
    {
        var ext = NewExt();
        ext.SaveConfig(new AiAdviceExternal.ExternalConfig("https://api.example.com", "k", []));
        File.WriteAllText(Path.Combine(_dataDir, "aiadvice.json"), "{broken");
        var (endpoint, hasKey, _) = ext.LoadConfigSafe();
        Assert.Equal("", endpoint);
        Assert.False(hasKey);
    }

    // ---- 未配置端点：400 ----

    [Fact]
    public async Task Analyze_NotConfigured_Throws400()
    {
        var ext = NewExt();
        var ex = await Assert.ThrowsAsync<AiAdviceExternal.AiExternalException>(
            () => ext.AnalyzeAsync("n1", "summary", "op", "Operator", "127.0.0.1"));
        Assert.Equal(400, ex.StatusCode);
    }

    // ---- 限频：每节点每分钟 ≤1 次 ----

    [Fact]
    public async Task Analyze_RateLimit_OnePerMinutePerNode()
    {
        static Task<HttpResponseMessage> Ok(HttpRequestMessage _, CancellationToken __) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"advice\"}}]}"),
            });
        var ext = NewExt(send: Ok, now: () => DateTimeOffset.UtcNow);
        ext.SaveConfig(new AiAdviceExternal.ExternalConfig("https://api.example.com/v1/chat", "k", ["api.example.com"]));

        await ext.AnalyzeAsync("n1", "s", "op", "Operator", "127.0.0.1");
        var ex = await Assert.ThrowsAsync<AiAdviceExternal.AiExternalException>(
            () => ext.AnalyzeAsync("n1", "s", "op", "Operator", "127.0.0.1"));
        Assert.Equal(429, ex.StatusCode);

        // 其他节点不受影响。
        await ext.AnalyzeAsync("n2", "s", "op", "Operator", "127.0.0.1");
    }

    // ---- SSRF：私网段默认拒绝 ----

    [Fact]
    public async Task Analyze_PrivateNetworkEndpoint_Blocked()
    {
        var ext = NewExt();
        ext.SaveConfig(new AiAdviceExternal.ExternalConfig("http://169.254.169.254/latest", "k", []));
        var ex = await Assert.ThrowsAsync<AiAdviceExternal.AiExternalException>(
            () => ext.AnalyzeAsync("n1", "s", "op", "Operator", "127.0.0.1"));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("blocked", ex.Message);
    }

    // ---- 审计：只记调用事实，不含 Key 与正文 ----

    [Fact]
    public async Task Analyze_AuditsCallFactsOnly()
    {
        static Task<HttpResponseMessage> Ok(HttpRequestMessage _, CancellationToken __) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"advice text\"}}]}"),
            });
        var ext = NewExt(send: Ok);
        ext.SaveConfig(new AiAdviceExternal.ExternalConfig("https://api.example.com/v1/chat", "secret-key", ["api.example.com"]));

        await ext.AnalyzeAsync("n1", "summary-with-secret-should-not-appear", "op", "Operator", "127.0.0.1");

        var events = _audit.Query(user: "op");
        var ev = Assert.Single(events, e => e.Action == "aiadvice.external");
        Assert.StartsWith("ok", ev.Result);
        Assert.DoesNotContain("secret-key", ev.Result);
        Assert.DoesNotContain("summary-with-secret", ev.Result);
        Assert.DoesNotContain("advice text", ev.Result);
    }

    // ---- 响应超限：1MB 上限 ----

    [Fact]
    public async Task Analyze_OversizedResponse_Rejected()
    {
        static Task<HttpResponseMessage> Huge(HttpRequestMessage _, CancellationToken __) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', AiAdviceExternal.MaxResponseBytes + 1024)),
            });
        var ext = NewExt(send: Huge);
        ext.SaveConfig(new AiAdviceExternal.ExternalConfig("https://api.example.com/v1/chat", "k", ["api.example.com"]));

        var ex = await Assert.ThrowsAsync<AiAdviceExternal.AiExternalException>(
            () => ext.AnalyzeAsync("n1", "s", "op", "Operator", "127.0.0.1"));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("1MB", ex.Message);
    }
}
