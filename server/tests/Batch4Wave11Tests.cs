using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Sec;
using TaoOps.Server.Services;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 批次四第十一波（门禁收口）：StreamIngestor.PruneFaulted 同通道双节点同轮故障——
/// faulted ??= 两臂（首故障赋值 / 次故障跳过赋值）双臂触达；另 BackupScheduleMonitor:131
/// 经核对为防御性不可达（唯一调用点 !p.Enabled 短路先行），入豁免清单。
/// </summary>
public class Batch4Wave11Tests
{
    [Fact]
    public async Task TwoNodesSameChannelFaulted_BothPrunedAndResubscribed()
    {
        // 同通道（WAF）两个节点的订阅任务同轮故障：PruneFaulted 循环首次走 ??= 赋值臂、
        // 次次走非空臂，两节点均移出已订集合并同轮重订（实效断言：重订数=2）。
        var registry = new NodeRegistry();
        registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a1", Hostname = "h1" });
        registry.Register(new TaoOps.Server.Grpc.RegisterRequest { AgentId = "a2", Hostname = "h2" });

        var wafTcs1 = new TaskCompletionSource();
        var wafTcs2 = new TaskCompletionSource();
        var pending = new Queue<TaskCompletionSource>();
        pending.Enqueue(wafTcs1);
        pending.Enqueue(wafTcs2);
        Task WafStart(string node, CancellationToken ct) =>
            pending.TryDequeue(out var tcs) ? tcs.Task : Task.CompletedTask;
        Task SecStart(string node, CancellationToken ct) => Task.CompletedTask;

        var svc = new StreamIngestorHostedService(registry, WafStart, SecStart,
            NullLogger<StreamIngestorHostedService>.Instance);

        // 首轮：两节点 WAF+SEC 全订阅。
        Assert.Equal(4, await svc.PollOnceAsync(CancellationToken.None));

        // 同轮双故障：PruneFaulted 两臂各走一次，两任务均移出。
        wafTcs1.TrySetException(new IOException("waf stream closed on a1"));
        wafTcs2.TrySetException(new IOException("waf stream closed on a2"));

        // 次轮：两节点 WAF 均重订（SEC 未故障不重订）。
        Assert.Equal(2, await svc.PollOnceAsync(CancellationToken.None));

        // 重订后去重。
        Assert.Equal(0, await svc.PollOnceAsync(CancellationToken.None));
    }
}
