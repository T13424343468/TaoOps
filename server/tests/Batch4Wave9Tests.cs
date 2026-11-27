using System.Text.Json;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using TaoOps.Server.Alerts;
using TaoOps.Server.Audit;
using TaoOps.Server.Services;
using TaoOps.Server.Services.K8s;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次四第九波：AdviceEngine mem-peak Critical 臂、K8sClusterService ListPods
/// 最小 Pod（无 namespace/spec/status）兜底臂、FindUnhealthy State 存在而 Waiting 缺失臂、
/// KubeClusterStore.List 有效/损坏/Clusters null 三态、KubeconfigParser 上下文缺失与集群缺失两 null 臂。
/// </summary>
public class Batch4Wave9Tests
{
    // ---- AdviceEngine：mem-peak Critical 臂（独立 FakeSource，口径同第七波） ----

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

    private static MetricsStore.MetricPoint P(long ts, double cpu = 10, ulong memUsed = 100, ulong memTotal = 1000) =>
        new(ts, cpu, memTotal, memUsed, 1, 1, 1, 1, 0, 0);

    [Fact]
    public async Task Advice_MemPeak_AtOrAboveCritical_IsCritical()
    {
        // 峰值 96%：≥95（85+10）→ Critical 臂（LevelName Critical 路径经 mem-peak 可达）。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var src = new FakeSource
        {
            Metrics = Enumerable.Range(0, 12).Select(i => P(now - (11 - i) * 60_000L, memUsed: 960, memTotal: 1000)).ToList()
        };
        var advices = await new AdviceEngine(src).AdviseAsync("n1", Thresholds);
        Assert.Equal("critical", advices.Single(x => x.Id == "mem-peak").Level);
    }

    // ---- K8s 侧第九波：直连假 API 服务器，不经 Web 宿主 ----

    [Collection("Integration")]
    public class K8sTailTests : IAsyncDisposable
    {
        private readonly FakeK8sApiServer _api = new();
        private readonly KubeClusterStore _store;
        private readonly K8sClusterService _svc;
        private readonly string _dataDir;

        public K8sTailTests()
        {
            _api.StartAsync().GetAwaiter().GetResult();
            _dataDir = Path.Combine(Path.GetTempPath(), $"k8sw9-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dataDir);
            _store = new KubeClusterStore(_dataDir);
            _store.Save("tail-cluster", _api.Kubeconfig, "v1.29.0");
            _svc = new K8sClusterService(_store, new K8sClientFactory());
        }

        public async ValueTask DisposeAsync()
        {
            _store.Remove("tail-cluster", confirm: true);
            await _api.DisposeAsync();
            Directory.Delete(_dataDir, recursive: true);
        }

        [Fact]
        public async Task ListPods_MinimalPod_NoNamespaceNoSpecNoStatus_FallsBackAllArms()
        {
            // metadata 只有 name：NamespaceProperty ?? ""、Spec?.NodeName ?? ""、Status?.Phase ?? "Unknown"、
            // ContainerStatuses 缺失 → restarts 0、ready false、waiting null，全兜底臂一次打穿。
            _api.OnJson("GET", "/api/v1/namespaces/default/pods", """
                {"items":[{"metadata":{"name":"bare"}}]}
                """);
            var pods = _svc.ListPods("tail-cluster", "default");
            var p = Assert.Single(pods);
            Assert.Equal("bare", p.Name);
            Assert.Equal("", p.Namespace);
            Assert.Equal("", p.Node);
            Assert.Equal("Unknown", p.Phase);
            Assert.Equal(0, p.Restarts);
            Assert.False(p.Ready);
            Assert.Null(p.Reason);
        }

        [Fact]
        public void ListPods_StatusPresentContainerStatusesNullOrEmpty_CoversReadyChainArms()
        {
            // Status 非空而 ContainerStatuses null → is { } false 臂；空数组 → Count > 0 false 臂。
            _api.OnJson("GET", "/api/v1/namespaces/default/pods", """
                {"items":[
                  {"metadata":{"name":"no-cs"},"status":{"phase":"Running"}},
                  {"metadata":{"name":"empty-cs"},"status":{"phase":"Running","containerStatuses":[]}},
                  {"metadata":{"name":"not-ready"},"status":{"phase":"Running","containerStatuses":[{"ready":false,"restartCount":0}]}}
                ]}
                """);
            var pods = _svc.ListPods("tail-cluster", "default");
            Assert.Equal("Running", pods[0].Phase);
            Assert.False(pods[0].Ready);
            Assert.False(pods[1].Ready);
            Assert.False(pods[2].Ready);
        }

        [Fact]
        public void FindUnhealthy_StatePresentWaitingNull_NoAlert()
        {
            // State 非空而 Waiting 缺失（Running 态）→ 链式 ?. 的 Waiting null 臂，不判不健康。
            var running = new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = "running-ok", NamespaceProperty = "ns9" },
                Status = new V1PodStatus
                {
                    ContainerStatuses = [new() { Name = "c", Ready = true, RestartCount = 0, State = new V1ContainerState { Running = new V1ContainerStateRunning() } }],
                },
            };
            Assert.Empty(K8sMonitor.FindUnhealthy([running]));

            // Status 非空而 ContainerStatuses null、Status 整体 null → 链式 ?. 两个 null 臂。
            var noCs = new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = "no-cs", NamespaceProperty = "ns9" },
                Status = new V1PodStatus(),
            };
            var noStatus = new V1Pod { Metadata = new V1ObjectMeta { Name = "no-status", NamespaceProperty = "ns9" } };
            // State 整体缺失的容器 → 谓词 c.State?. null 臂。
            var noState = new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = "no-state", NamespaceProperty = "ns9" },
                Status = new V1PodStatus { ContainerStatuses = [new() { Name = "c", Ready = true, RestartCount = 0 }] },
            };
            Assert.Empty(K8sMonitor.FindUnhealthy([noCs, noStatus, noState]));
        }

        [Fact]
        public void Store_List_ValidCorruptAndNullClusters_ThreeStates()
        {
            // 有效清单 → 正常反序列化；损坏 JSON → JsonException 兜底；Clusters null → ?? [] 兜底。
            var dir = Path.Combine(_dataDir, "store-list-check");
            var root = Path.Combine(dir, "k8s", "clusters");
            Directory.CreateDirectory(root);

            File.WriteAllText(Path.Combine(root, "clusters.json"),
                """{"clusters":[{"name":"c1","server":"https://1.2.3.4:6443","version":"v1.29.0","addedMs":1700000000000}]}""");
            var ok = new KubeClusterStore(dir).List();
            var meta = Assert.Single(ok);
            Assert.Equal("c1", meta.Name);
            Assert.Equal("https://1.2.3.4:6443", meta.Server);

            File.WriteAllText(Path.Combine(root, "clusters.json"), "{not json");
            Assert.Empty(new KubeClusterStore(dir).List());

            File.WriteAllText(Path.Combine(root, "clusters.json"), """{"clusters":null}""");
            Assert.Empty(new KubeClusterStore(dir).List());

            // 文件内容为 null 字面量 → Deserialize 返回 null → ?. null 臂。
            File.WriteAllText(Path.Combine(root, "clusters.json"), "null");
            Assert.Empty(new KubeClusterStore(dir).List());
        }

        [Fact]
        public void KubeconfigParser_CurrentContextGhostOrClusterMissing_ReturnsNull()
        {
            // current-context 指向不存在的 context → contexts.TryGetValue false 臂 → null。
            const string ghost = """
                current-context: ghost
                contexts:
                  - name: ctx1
                    cluster: c1
                clusters:
                  - name: c1
                    server: https://1.2.3.4:6443
                """;
            Assert.Null(KubeconfigParser.ExtractServer(ghost));

            // context 存在但指向未登记集群 → clusters.TryGetValue false 臂 → null。
            const string missingCluster = """
                current-context: ctx1
                contexts:
                  - name: ctx1
                    cluster: nope
                clusters:
                  - name: c1
                    server: https://1.2.3.4:6443
                """;
            Assert.Null(KubeconfigParser.ExtractServer(missingCluster));
        }
    }
}
