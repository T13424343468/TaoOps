using System.Text.Json;
using TaoOps.Server.Alerts;
using TaoOps.Server.Push;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// FR-A20 推送分发器与告警接线：critical 触发、重试退避、逐次留痕、
/// 渠道启停、非 critical 不触发。
/// </summary>
public class PushDispatcherTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"taoops-push-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    private (PushChannelStore, SsrfWhitelistStore, PushLogStore) Stores() =>
        (new PushChannelStore(_dataDir), new SsrfWhitelistStore(_dataDir), new PushLogStore(_dataDir));

    private static Alert Alert(string level = "critical") => new()
    {
        Id = "a1", Source = "disk", Level = level, NodeId = "n1",
        Detail = "disk 95%", Ts = 1700000000000,
    };

    [Fact]
    public async Task SendAsync_EnabledChannel_PostsAndLogs()
    {
        var (channels, whitelist, log) = Stores();
        var bodies = new List<string>();
        var dispatcher = new PushDispatcher(channels, whitelist, log,
            clientFactory: () => new HttpClient(new StubHandler(_ =>
            {
                bodies.Add("hit");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            })));
        channels.Upsert(new PushChannel { Name = "hook", Url = "http://hook.example/x" });

        var results = await dispatcher.SendAsync(Alert());
        Assert.Single(results);
        Assert.True(results[0].Ok);
        Assert.Equal(1, results[0].Attempts);
        Assert.Single(bodies);
        var entries = log.Query();
        Assert.Single(entries);
        Assert.True(entries[0].Ok);
        Assert.Equal(200, entries[0].StatusCode);
    }

    [Fact]
    public async Task SendAsync_FailureRetriesThreeTimes_WithBackoff()
    {
        var (channels, whitelist, log) = Stores();
        var delays = new List<int>();
        var dispatcher = new PushDispatcher(channels, whitelist, log,
            clientFactory: () => new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))),
            delay: () => { delays.Add(1); return Task.CompletedTask; });
        channels.Upsert(new PushChannel { Name = "hook", Url = "http://hook.example/x" });

        var results = await dispatcher.SendAsync(Alert());
        Assert.False(results[0].Ok);
        Assert.Equal(4, results[0].Attempts); // 首次 + 3 次重试
        Assert.Equal(3, delays.Count);        // 退避 1s/2s/4s
        Assert.Equal(4, log.Query().Count);   // 每次尝试独立留痕
    }

    [Fact]
    public async Task SendAsync_DisabledChannel_Skipped()
    {
        var (channels, whitelist, log) = Stores();
        var dispatcher = new PushDispatcher(channels, whitelist, log,
            clientFactory: () => new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK))));
        channels.Upsert(new PushChannel { Name = "off", Url = "http://hook.example/x", Enabled = false });

        var results = await dispatcher.SendAsync(Alert());
        Assert.Empty(results);
        Assert.Empty(log.Query());
    }

    [Fact]
    public void OnAlert_Critical_TriggersFireAndForget()
    {
        var (channels, whitelist, log) = Stores();
        var dispatcher = new PushDispatcher(channels, whitelist, log,
            clientFactory: () => new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK))));
        channels.Upsert(new PushChannel { Name = "hook", Url = "http://hook.example/x" });

        dispatcher.OnAlert(Alert("critical"));
        // fire-and-forget：日志最终出现（轮询等待异步完成）。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (log.Query().Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.Single(log.Query());
    }

    [Fact]
    public void OnAlert_NonCritical_DoesNotTrigger()
    {
        var (channels, whitelist, log) = Stores();
        var dispatcher = new PushDispatcher(channels, whitelist, log,
            clientFactory: () => new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK))));
        channels.Upsert(new PushChannel { Name = "hook", Url = "http://hook.example/x" });

        dispatcher.OnAlert(Alert("warn"));
        dispatcher.OnAlert(Alert("info"));
        Thread.Sleep(300);
        Assert.Empty(log.Query());
    }

    [Fact]
    public async Task AlertCenter_CriticalAlert_TriggersPush()
    {
        var (channels, whitelist, log) = Stores();
        var dispatcher = new PushDispatcher(channels, whitelist, log,
            clientFactory: () => new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK))));
        channels.Upsert(new PushChannel { Name = "hook", Url = "http://hook.example/x" });

        var store = new AlertStore(_dataDir);
        var center = new AlertCenter(store, dispatcher);
        center.Raise("disk", AlertLevels.Critical, "n1", "disk 95%");
        center.Raise("intrusion", AlertLevels.Warn, "n1", "probe");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (log.Query().Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(50);
        Assert.Single(log.Query()); // 仅 critical 触发
        Assert.Single(store.Query()); // 告警照常落账
    }

    [Fact]
    public void ChannelStore_UpsertUpdateDelete_RoundTrip()
    {
        var (channels, _, _) = Stores();
        var created = channels.Upsert(new PushChannel { Name = "a", Url = "http://a.example" });
        Assert.False(string.IsNullOrEmpty(created.Id));
        var updated = channels.Upsert(new PushChannel { Id = created.Id, Name = "a2", Url = "http://a.example/2" });
        Assert.Equal("a2", updated.Name);
        Assert.Equal(created.CreatedMs, updated.CreatedMs);
        Assert.True(channels.Delete(created.Id));
        Assert.Empty(channels.List());
        Assert.Throws<KeyNotFoundException>(() =>
            channels.Upsert(new PushChannel { Id = "missing", Name = "x", Url = "http://x" }));
    }

    [Fact]
    public void WhitelistStore_Replace_DedupesAndReturnsDiff()
    {
        var (whitelist, _) = Stores();
        var (oldEntries, newEntries) = whitelist.Replace(["10.0.0.0/8", "10.0.0.0/8", "host.example"]);
        Assert.Empty(oldEntries);
        Assert.Equal(2, newEntries.Count);
        var (_, afterClear) = whitelist.Replace([]);
        Assert.Empty(afterClear);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
