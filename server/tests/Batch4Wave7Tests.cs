using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Audit;
using TaoOps.Server.Alerts;
using TaoOps.Server.Services;
using TaoOps.Server.Services.K8s;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次四第七波：AdviceEngine 级别/缺省臂、AiAdviceExternal 异常与空配置臂、
/// ListPods 无状态容器臂、FindUnhealthy 剩余缺省臂、K8sMonitor 后台循环可达段。
/// </summary>
public class Batch4Wave7Tests
{
    // ---- AdviceEngine：级别臂与缺省臂（独立 FakeSource，口径同 AdviceEngineTests） ----

    private sealed class FakeSource : IAdviceDataSource
    {
        public IReadOnlyList<MetricsStore.MetricPoint> Metrics { get; set; } = [];
        public IReadOnlyList<Alert> Alerts { get; set; } = [];
        public IReadOnlyList<CertStatusInfo> Certs { get; set; } = [];
        public int BackupCoverage { get; set; } = -1;
        public int Restarts { get; set; } = -1;

        public IReadOnlyList<MetricsStore.MetricPoint> GetMetrics(string nodeId, long sinceMs) => Metrics;
        public IReadOnlyList<Alert> GetAlertHistory(string nodeId, long sinceMs) => Alerts;
        public IReadOnlyList<CertStatusInfo> GetCertStatus(string nodeId) => Certs;
        public int GetBackupCoverage(string nodeId) => BackupCoverage;
        public Task<int> GetContainerRestarts(string nodeId, CancellationToken ct = default) => Task.FromResult(Restarts);
    }

    private static readonly Settings.AlertThresholdSettings Thresholds = new();

    private static MetricsStore.MetricPoint P(long ts, double cpu = 10, ulong memUsed = 100, ulong memTotal = 1000,
        ulong diskUsed = 0, ulong diskTotal = 0) =>
        new(ts, cpu, memTotal, memUsed, 1, 1, 1, 1, diskUsed, diskTotal);

    [Fact]
    public async Task Advice_CpuPeak_BetweenThresholdAndCritical_IsWarn()
    {
        // 峰值 90：≥85 触发但 <95（85+10）→ warn 臂。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource { Metrics = Enumerable.Range(0, 12).Select(i => P(now - (11 - i) * 60_000L, cpu: 90)).ToList() };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.Equal("warn", advices.Single(x => x.Id == "cpu-peak").Level);
    }

    [Fact]
    public async Task Advice_MemTotalZero_MemRatioTreatedAsZero()
    {
        // MemTotalKB=0 → 比率 lambda false 臂（按 0 计），不出内存建议。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource { Metrics = Enumerable.Range(0, 12).Select(i => P(now - i * 60_000L, memUsed: 500, memTotal: 0)).ToList() };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.DoesNotContain(advices, a => a.Id == "mem-peak");
    }

    [Fact]
    public async Task Advice_MemPeak_BetweenThresholdAndCritical_IsWarn()
    {
        // 峰值 88%：≥85 触发但 <95 → warn 臂。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource { Metrics = Enumerable.Range(0, 12).Select(i => P(now - (11 - i) * 60_000L, memUsed: 880, memTotal: 1000)).ToList() };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.Equal("warn", advices.Single(x => x.Id == "mem-peak").Level);
    }

    [Fact]
    public async Task Advice_DiskSlope_AlreadyPastWatermark_PredictsZeroDays()
    {
        // 30 点、跨度 >0.2 天、正斜率、当前用量已过 90% 水位 → remainKb ≤ 0 → 预计 0 天（: 0 臂）。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource
        {
            Metrics = Enumerable.Range(0, 30).Select(i => P(
                now - (29 - i) * 3_600_000L,
                diskUsed: (ulong)(80 + (double)i / 29 * 15),
                diskTotal: 100)).ToList(),
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        var a = advices.Single(x => x.Id == "disk-watermark");
        Assert.Contains("预计 0 天内", a.Basis);
        Assert.Equal("critical", a.Level); // 95% ≥ 90
    }

    [Fact]
    public async Task Advice_Cert_ExpiringWithin7Days_IsCritical()
    {
        var src = new FakeSource
        {
            Certs = [new CertStatusInfo("short.example.com", DateTimeOffset.UtcNow.AddDays(5), false)],
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.Equal("critical", advices.Single(x => x.Id == "cert-short.example.com").Level);
    }

    [Fact]
    public async Task Advice_Cert_AutoRenewConfigured_ShowsRenewNote()
    {
        // AutoRenewConfigured=true 臂（既有用例只覆盖 false 臂）。
        var src = new FakeSource
        {
            Certs = [new CertStatusInfo("renew.example.com", DateTimeOffset.UtcNow.AddDays(10), true)],
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        var a = advices.Single(x => x.Id == "cert-renew.example.com");
        Assert.Contains("自动续期已配置", a.Basis);
        Assert.Equal("warn", a.Level); // 10 天 > 7
    }

    [Fact]
    public async Task Advice_ContainerRestarts_AtLeastTen_IsCritical()
    {
        var src = new FakeSource { Restarts = 12 };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.Equal("critical", advices.Single(x => x.Id == "container-restarts").Level);
    }

    // ---- AiAdviceExternal：空配置/损坏配置/外呼异常/非成功状态臂 ----

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "taoops-aiext-w7-" + Guid.NewGuid().ToString("N"));

    private AiAdviceExternal NewExt(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null) =>
        new(_dataDir, new AuditStore(_dataDir), send);

    [Fact]
    public void LoadConfigSafe_FileMissing_ReturnsDefaults()
    {
        // 文件不存在 → 提前返回臂。
        var (endpoint, hasKey, whitelist) = NewExt().LoadConfigSafe();
        Assert.Equal("", endpoint);
        Assert.False(hasKey);
        Assert.Empty(whitelist);
    }

    [Fact]
    public void LoadConfigSafe_NullJsonDocument_UsesNullCfgArms()
    {
        // 文件内容为字面量 null → 反序列化得 null → 三个 ?? 右臂。
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, "aiadvice.json"), "null");
        var (endpoint, hasKey, whitelist) = NewExt().LoadConfigSafe();
        Assert.Equal("", endpoint);
        Assert.False(hasKey);
        Assert.Empty(whitelist);
    }

    [Fact]
    public async Task Analyze_LoadFullCorruptedJson_ReturnsNull_ThrowsNotConfigured()
    {
        // LoadFull 的 JsonException 臂 → null → 400 not configured。
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, "aiadvice.json"), "{broken");
        var ex = await Assert.ThrowsAsync<AiAdviceExternal.AiExternalException>(
            () => NewExt().AnalyzeAsync("n1", "s", "op", "Operator", "127.0.0.1"));
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Analyze_SendThrowsUnexpectedException_AuditsError_AndThrows502()
    {
        static Task<HttpResponseMessage> Boom(HttpRequestMessage _, CancellationToken __) =>
            throw new InvalidOperationException("socket exploded");
        var ext = NewExt(Boom);
        ext.SaveConfig(new AiAdviceExternal.ExternalConfig("https://api.example.com/v1/chat", "k", ["api.example.com"]));

        var ex = await Assert.ThrowsAsync<AiAdviceExternal.AiExternalException>(
            () => ext.AnalyzeAsync("n1", "s", "op", "Operator", "127.0.0.1"));
        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("unreachable", ex.Message);
        var ev = Assert.Single(new AuditStore(_dataDir).Query(user: "op"));
        Assert.StartsWith("error", ev.Result);
    }

    [Fact]
    public async Task Analyze_EndpointReturns500_Throws502NonSuccess()
    {
        static Task<HttpResponseMessage> ServerError(HttpRequestMessage _, CancellationToken __) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var ext = NewExt(ServerError);
        ext.SaveConfig(new AiAdviceExternal.ExternalConfig("https://api.example.com/v1/chat", "k", ["api.example.com"]));

        var ex = await Assert.ThrowsAsync<AiAdviceExternal.AiExternalException>(
            () => ext.AnalyzeAsync("n1", "s", "op", "Operator", "127.0.0.1"));
        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("non-success status", ex.Message);
    }

    [Fact]
    public void SsrfHttpClient_Instance_Initializes()
    {
        // 静态实例字段初始化器入账（外呼兜底通道，测试不实际外呼）。
        Assert.NotNull(SsrfHttpClient.Instance);
    }
}

/// <summary>
/// K8s 侧第七波：ListPods 无状态容器臂、FindUnhealthy 剩余缺省臂、后台循环可达段。
/// 走集成夹具（共享 TAOOPS_DATA_DIR），口径同 K8sServiceBackfillTests。
/// </summary>
[Collection("Integration")]
public class Batch4Wave7K8sTests : IDisposable
{
    private readonly K8sFakeApiFactory _factory;
    private readonly HttpClient _client;
    private readonly string _dataDir;
    private readonly List<string> _seeded = [];

    public Batch4Wave7K8sTests()
    {
        _factory = new K8sFakeApiFactory();
        _factory.Api.StartAsync().GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", _factory.LoginAdminAsync().GetAwaiter().GetResult());
        _dataDir = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR")!;
    }

    public void Dispose()
    {
        foreach (var name in _seeded) new KubeClusterStore(_dataDir).Remove(name, confirm: true);
        _factory.Dispose();
        _factory.Api.DisposeAsync().GetAwaiter().GetResult();
    }

    private void SeedCluster(string name)
    {
        new KubeClusterStore(_dataDir).Save(name, _factory.Api.Kubeconfig, "v1.29.0");
        _seeded.Add(name);
    }

    [Fact]
    public async Task Pods_List_StatelessContainer_WaitingNullAndNotReady()
    {
        // containerStatuses 条目无 state → 判定 lambda false 臂、FirstOrDefault 空转、ready false。
        SeedCluster("stateless-pod");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """
            {"items":[
              {"metadata":{"name":"stateless","namespace":"default"},
               "status":{"phase":"Running","containerStatuses":[
                 {"name":"c","ready":false,"restartCount":2}]}}
            ]}
            """);
        var res = await _client.GetAsync("/api/k8s/stateless-pod/pods?ns=default");
        res.EnsureSuccessStatusCode();
        var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var p = items.Single(x => x.GetProperty("name").GetString() == "stateless");
        Assert.Equal(2, p.GetProperty("restarts").GetInt32());
        Assert.False(p.GetProperty("ready").GetBoolean());
        Assert.True(p.GetProperty("reason").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void FindUnhealthy_StatusNull_EmptyCs_StateNull_NoAlerts()
    {
        var statusNull = new V1Pod { Metadata = new V1ObjectMeta { Name = "no-status", NamespaceProperty = "ns1" } };
        var emptyCs = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "empty-cs", NamespaceProperty = "ns1" },
            Status = new V1PodStatus { ContainerStatuses = [] }, // FirstOrDefault 空转 → ?. 短路臂
        };
        var stateNull = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "state-null", NamespaceProperty = "ns1" },
            Status = new V1PodStatus
            {
                ContainerStatuses = [new() { Name = "c", Ready = false, RestartCount = 0, State = null }], // lambda State null 臂
            },
        };
        var result = K8sMonitor.FindUnhealthy([statusNull, emptyCs, stateNull]);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteAsync_PreCancelledToken_ExitsWithoutScanning()
    {
        // 启动前已取消 → while 首判 false → 直接返回（循环条件 false 臂）。
        var dataDir = Path.Combine(Path.GetTempPath(), $"k8smon7a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var monitor = new K8sMonitor(new KubeClusterStore(dataDir), new K8sClientFactory(),
                new AlertCenter(new AlertStore(dataDir)), NullLogger<Services.K8s.K8sMonitor>.Instance);
            await monitor.StartAsync(new CancellationToken(canceled: true));
            await monitor.StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsOneScan_ThenStopsCleanly()
    {
        // 空集群库跑一轮 ScanOnce 后进入周期延迟；StopAsync 取消延迟并收尾（循环体与延迟 await 臂）。
        var dataDir = Path.Combine(Path.GetTempPath(), $"k8smon7b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var monitor = new K8sMonitor(new KubeClusterStore(dataDir), new K8sClientFactory(),
                new AlertCenter(new AlertStore(dataDir)), NullLogger<Services.K8s.K8sMonitor>.Instance);
            using var cts = new CancellationTokenSource();
            _ = monitor.StartAsync(cts.Token);
            await Task.Delay(300); // 让首轮 ScanOnce 完成、进入 Task.Delay
            try { await monitor.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { /* 取消延迟以 OCE 收尾，属预期 */ }
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
