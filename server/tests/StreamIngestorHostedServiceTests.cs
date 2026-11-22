using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Sec;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 断言 A4：事件回流自动订阅——节点上线后自动补订 WAF/SEC 两路；
/// 流未就绪静默重试；订阅故障后重订；两路独立互不拖累。
/// </summary>
public class StreamIngestorHostedServiceTests
{
    private static StreamIngestorHostedService NewService(NodeRegistry registry,
        Func<string, CancellationToken, Task> waf, Func<string, CancellationToken, Task> sec) =>
        new(registry, waf, sec, NullLogger<StreamIngestorHostedService>.Instance);

    [Fact]
    public async Task StreamNotAttached_PollSkipsWithoutCrash_AndRetriesLater()
    {
        // 流未挂（GetLog 为 null → InvalidOperationException）：本轮 0 订阅、不崩；流挂上后下轮补订。
        var registry = new NodeRegistry();
        registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a1", Hostname = "h1" });
        var attached = false;
        var calls = 0;
        Task Start(string node, CancellationToken ct)
        {
            calls++;
            if (!attached) throw new InvalidOperationException("not attached");
            return Task.CompletedTask;
        }

        var svc = NewService(registry, Start, Start);
        Assert.Equal(0, await svc.PollOnceAsync(CancellationToken.None));

        // 流挂上后下一轮补订成功。
        attached = true;
        Assert.Equal(2, await svc.PollOnceAsync(CancellationToken.None));
        Assert.Equal(4, calls); // waf+sec 各重试一次
    }

    [Fact]
    public async Task SubscribedNodes_NotResubscribed()
    {
        // 订阅去重：同一节点不重复订阅。
        var registry = new NodeRegistry();
        registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a1", Hostname = "h1" });
        var calls = 0;
        var svc = NewService(registry, (n, ct) => { calls++; return Task.CompletedTask; },
            (n, ct) => { calls++; return Task.CompletedTask; });

        Assert.Equal(2, await svc.PollOnceAsync(CancellationToken.None));
        Assert.Equal(0, await svc.PollOnceAsync(CancellationToken.None));
        Assert.Equal(0, await svc.PollOnceAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FaultedSubscription_IsResubscribedOnNextPoll()
    {
        // 订阅半路故障（流断开）：任务移出已订集合，下一轮重订；重订后去重。
        // 两路各自独立持有会故障的首订任务，故障后均应重订（实效断言：重订数=2）。
        var registry = new NodeRegistry();
        registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a1", Hostname = "h1" });
        var wafTcs = new TaskCompletionSource();
        var secTcs = new TaskCompletionSource();
        var wafFirst = true;
        var secFirst = true;
        Task WafStart(string node, CancellationToken ct)
        {
            if (wafFirst) { wafFirst = false; return wafTcs.Task; }
            return Task.CompletedTask;
        }
        Task SecStart(string node, CancellationToken ct)
        {
            if (secFirst) { secFirst = false; return secTcs.Task; }
            return Task.CompletedTask;
        }

        var svc = NewService(registry, WafStart, SecStart);
        Assert.Equal(2, await svc.PollOnceAsync(CancellationToken.None));

        wafTcs.TrySetException(new IOException("waf stream closed"));
        secTcs.TrySetException(new IOException("sec stream closed"));
        Assert.Equal(2, await svc.PollOnceAsync(CancellationToken.None)); // 两路故障后均重订
        Assert.Equal(0, await svc.PollOnceAsync(CancellationToken.None)); // 重订后去重
    }

    [Fact]
    public async Task OneChannelFailing_DoesNotBlockTheOther()
    {
        // 两路独立：WAF 一直失败不拖累 SEC 订阅成功。
        var registry = new NodeRegistry();
        registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a1", Hostname = "h1" });
        var secCalls = 0;
        Task WafFail(string node, CancellationToken ct) => throw new InvalidOperationException("no waf stream");
        Task SecOk(string node, CancellationToken ct) { secCalls++; return Task.CompletedTask; }

        var svc = NewService(registry, WafFail, SecOk);
        Assert.Equal(1, await svc.PollOnceAsync(CancellationToken.None));
        Assert.Equal(1, secCalls);
        // 第二轮 SEC 去重不再调用（实效断言：重跑一轮，SEC 调用数不变）。
        Assert.Equal(0, await svc.PollOnceAsync(CancellationToken.None));
        Assert.Equal(1, secCalls);
    }
}
