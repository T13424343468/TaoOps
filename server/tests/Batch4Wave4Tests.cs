using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using k8s.Models;
using TaoOps.Server.Services.K8s;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账批次四第四波：K8sClusterService 缺省字段映射（节点/工作负载/Pod 空状态兜底）、
/// version 探测 GitVersion 缺失回退、ParsePodMetrics 单位换算与缺字段全臂、
/// RolloutStatus/Scale 空状态臂、K8sMonitor.FindUnhealthy 判定全臂。
/// 端点侧走假 K8s API 服务器；ParsePodMetrics/FindUnhealthy 为纯函数直测。
/// </summary>
[Collection("Integration")]
public class Batch4Wave4Tests : IDisposable
{
    private readonly K8sFakeApiFactory _factory;
    private readonly HttpClient _client;
    private readonly string _dataDir;
    private readonly List<string> _seeded = new();

    public Batch4Wave4Tests()
    {
        _factory = new K8sFakeApiFactory();
        _factory.Api.StartAsync().GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", _factory.LoginAdminAsync().GetAwaiter().GetResult());
        _dataDir = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR")!;
    }

    public void Dispose()
    {
        // 共享集群库按主机实例隔离：种过的集群一律删除，避免污染同 Collection 的空清单断言。
        foreach (var name in _seeded) new KubeClusterStore(_dataDir).Remove(name, confirm: true);
        _factory.Dispose();
        _factory.Api.DisposeAsync().GetAwaiter().GetResult();
    }

    private void SeedCluster(string name)
    {
        new KubeClusterStore(_dataDir).Save(name, _factory.Api.Kubeconfig, "v1.29.0");
        _seeded.Add(name);
    }

    // ---- 接入：version 探测 GitVersion 缺失 → Major.Minor 回退臂 ----

    [Fact]
    public async Task AddCluster_VersionFallback_WithoutGitVersion()
    {
        _seeded.Add("ver-fallback"); // 走端点落库，Dispose 一并清理
        _factory.Api.OnJson("GET", "/version", """{"major":"1","minor":"29"}""");
        var res = await _client.PostAsJsonAsync("/api/k8s/clusters",
            new { name = "ver-fallback", kubeconfig = _factory.Api.Kubeconfig });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.29", body.GetProperty("version").GetString());
    }

    // ---- 只读层：缺省字段全兜底臂 ----

    [Fact]
    public async Task Nodes_BareItem_MapDefaults()
    {
        SeedCluster("bare-nodes");
        _factory.Api.OnJson("GET", "/api/v1/nodes", """{"items":[{"metadata":{"name":"bare"},"status":{}}]}""");
        var res = await _client.GetAsync("/api/k8s/bare-nodes/nodes");
        res.EnsureSuccessStatusCode();
        var n = (await res.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal("NotReady", n.GetProperty("status").GetString()); // 无 conditions → NotReady 臂
        Assert.Equal(0, n.GetProperty("cpuCores").GetDouble()); // 无 capacity.cpu → 0 臂
        Assert.Equal("", n.GetProperty("memory").GetString()); // 无 capacity.memory → "" 臂
        Assert.Equal("", n.GetProperty("version").GetString()); // 无 nodeInfo → "" 臂
    }

    [Fact]
    public async Task Workloads_BareItems_MapDefaults()
    {
        SeedCluster("bare-wl");
        _factory.Api.OnJson("GET", "/apis/apps/v1/deployments", """{"items":[{"metadata":{"name":"d1"},"spec":{"template":{"spec":{"containers":[]}}},"status":{}}]}""");
        _factory.Api.OnJson("GET", "/apis/apps/v1/statefulsets", """{"items":[{"metadata":{"name":"s1"},"spec":{"template":{"spec":{"containers":[]}}},"status":{}}]}""");
        _factory.Api.OnJson("GET", "/apis/apps/v1/daemonsets", """{"items":[{"metadata":{"name":"ds1"},"spec":{"template":{"spec":{"containers":[]}}},"status":{}}]}""");
        var res = await _client.GetAsync("/api/k8s/bare-wl/workloads");
        res.EnsureSuccessStatusCode();
        var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal("", i.GetProperty("namespace").GetString()); // NamespaceProperty ?? "" 臂
            Assert.Equal(0, i.GetProperty("ready").GetInt32()); // Status.ReadyReplicas ?? 0 臂
            Assert.Equal(0, i.GetProperty("desired").GetInt32()); // Spec.Replicas ?? 0 臂
        });
    }

    [Fact]
    public async Task Pods_BareAndEmptyContainers_MapDefaults()
    {
        SeedCluster("bare-pods");
        // 无 status/spec → phase Unknown、restarts 0、ready false、node ""。
        // 有 status 但 containerStatuses 空数组 → ready false（Count>0 短路臂）、waiting null。
        _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}}]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """
            {"items":[
              {"metadata":{"name":"bare-pod"}},
              {"metadata":{"name":"empty-cs"},"status":{"phase":"Running","containerStatuses":[]}}
            ]}
            """);
        var res = await _client.GetAsync("/api/k8s/bare-pods/pods");
        res.EnsureSuccessStatusCode();
        var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var bare = items.Single(x => x.GetProperty("name").GetString() == "bare-pod");
        Assert.Equal("Unknown", bare.GetProperty("phase").GetString());
        Assert.Equal(0, bare.GetProperty("restarts").GetInt32());
        Assert.False(bare.GetProperty("ready").GetBoolean());
        Assert.Equal("", bare.GetProperty("node").GetString());
        var empty = items.Single(x => x.GetProperty("name").GetString() == "empty-cs");
        Assert.False(empty.GetProperty("ready").GetBoolean());
    }

    // ---- 操作层：RolloutStatus/Scale 空状态臂 ----

    [Fact]
    public async Task RolloutStatus_MissingAndPartialArms()
    {
        SeedCluster("roll-arms");
        // 无 spec.replicas（spec 对象在）→ desired 0 → complete false（desired>0 短路臂）。
        _factory.Api.OnJson("GET", "/apis/apps/v1/namespaces/default/deployments/web/status",
            """{"metadata":{"name":"web"},"spec":{},"status":{}}""");
        var none = await _client.PostAsJsonAsync("/api/k8s/roll-arms/ops",
            new { op = "rollout_status", ns = "default", deployment = "web" });
        none.EnsureSuccessStatusCode();
        Assert.False((await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("complete").GetBoolean());

        // updated 达标但 available 不足 → complete false（available 臂）。
        _factory.Api.Reset();
        _factory.Api.OnJson("GET", "/apis/apps/v1/namespaces/default/deployments/web/status", """
            {"metadata":{"name":"web"},"spec":{"replicas":3},
             "status":{"updatedReplicas":3,"availableReplicas":2}}
            """);
        var partial = await _client.PostAsJsonAsync("/api/k8s/roll-arms/ops",
            new { op = "rollout_status", ns = "default", deployment = "web" });
        partial.EnsureSuccessStatusCode();
        Assert.False((await partial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Scale_ResponseWithoutStatus_MapsZero()
    {
        SeedCluster("scale-bare");
        _factory.Api.OnJson("PATCH", "/apis/apps/v1/namespaces/default/deployments/web/scale",
            """{"metadata":{"name":"web"}}""");
        var res = await _client.PostAsJsonAsync("/api/k8s/scale-bare/ops",
            new { op = "scale", ns = "default", deployment = "web", replicas = "3" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("desired").GetInt32());
        Assert.Equal(0, body.GetProperty("ready").GetInt32()); // Status?.Replicas ?? 0 臂
    }

    // ---- 健康总览：unhealthy 判定含 waiting 原因臂 ----

    [Fact]
    public async Task Health_FlagsWaitingReasonPods()
    {
        SeedCluster("health-wait");
        _factory.Api.OnJson("GET", "/api/v1/nodes", """{"items":[]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}}]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """
            {"items":[{"metadata":{"name":"bad","namespace":"default"},
              "status":{"phase":"Running","containerStatuses":[
                {"name":"app","ready":false,"restartCount":0,"state":{"waiting":{"reason":"ImagePullBackOff"}}}]}}]}
            """);
        var res = await _client.GetAsync("/api/k8s/health");
        res.EnsureSuccessStatusCode();
        var entry = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Single(x => x.GetProperty("cluster").GetString() == "health-wait");
        Assert.Equal(1, entry.GetProperty("podsTotal").GetInt32());
        Assert.Contains("default/bad", entry.GetProperty("unhealthyPods").EnumerateArray()
            .Select(x => x.GetString()!).ToList()); // Reason 非空即不健康（ImagePullBackOff 臂）
    }

    // ---- ParsePodMetrics：单位换算与缺字段全臂（纯函数直测） ----

    [Fact]
    public void ParsePodMetrics_Units_AndMissingFields()
    {
        // 无 items 键 → Available=true 空清单（0/2 臂）。
        var noItems = K8sClusterService.ParsePodMetrics(new { foo = 1 });
        Assert.True(noItems.Available);
        Assert.Empty(noItems.Pods);

        // items 非数组 → 空清单臂。
        var notArray = K8sClusterService.ParsePodMetrics(new { items = "x" });
        Assert.Empty(notArray.Pods);

        // 单位换算全臂：m/u/n/裸数/非法/缺失；内存 Ki/Gi/Ti/裸数/非法/缺失。
        var full = K8sClusterService.ParsePodMetrics(new
        {
            items = new object[]
            {
                new // 全字段容器：cpu 五种 + mem 五种
                {
                    metadata = new { name = "p1", @namespace = "ns1" },
                    containers = new object[]
                    {
                        new { name = "c1", usage = new { cpu = "250m", memory = "512Ki" } },
                        new { name = "c2", usage = new { cpu = "1500u", memory = "2Gi" } },
                        new { name = "c3", usage = new { cpu = "2000000n", memory = "1Ti" } },
                        new { name = "c4", usage = new { cpu = "1.5", memory = "100" } },
                        new { name = "c5", usage = new { cpu = "abc", memory = "abc" } },
                        new { name = "c6", usage = new { } },
                    },
                },
                new { metadata = new { name = "p2" } }, // 无 namespace、无 containers
            },
        });
        Assert.True(full.Available);
        Assert.Equal(2, full.Pods.Count);
        var p1 = full.Pods[0];
        Assert.Equal("ns1", p1.Namespace);
        // cpu 汇总：250 + 2 + 2 + 1500 + 0 + 0 = 1754 毫核。
        Assert.Equal(1754, p1.CpuMilli);
        // mem 汇总：0.5 + 2048 + 1048576 + 100 + 0 + 0 = 1050724.5（容器各自取整后求和，本值已是一位小数）。
        Assert.Equal(1050724.5, p1.MemMiB);
        var c1 = p1.Containers.First(x => x.Name == "c1");
        Assert.Equal(250, c1.CpuMilli);
        Assert.Equal(0.5, c1.MemMiB);
        var c2 = p1.Containers.First(x => x.Name == "c2");
        Assert.Equal(2, c2.CpuMilli); // u → 毫核向上取整
        Assert.Equal(2048, c2.MemMiB);
        var c3 = p1.Containers.First(x => x.Name == "c3");
        Assert.Equal(2, c3.CpuMilli); // n → 毫核向上取整
        Assert.Equal(1024 * 1024, c3.MemMiB);
        var c4 = p1.Containers.First(x => x.Name == "c4");
        Assert.Equal(1500, c4.CpuMilli); // 裸核数 → ×1000
        Assert.Equal(100, c4.MemMiB); // 无 i 后缀按原值
        var c5 = p1.Containers.First(x => x.Name == "c5");
        Assert.Equal(0, c5.CpuMilli); // 非法 → 0
        Assert.Equal(0, c5.MemMiB);
        var c6 = p1.Containers.First(x => x.Name == "c6");
        Assert.Equal(0, c6.CpuMilli); // usage 缺字段 → null → 0
        Assert.Equal(0, c6.MemMiB);
        var p2 = full.Pods[1];
        Assert.Equal("", p2.Namespace); // namespace 缺失 → "" 臂
        Assert.Empty(p2.Containers); // containers 缺失 → 空臂
        Assert.Equal(0, p2.CpuMilli);
    }

    // ---- K8sMonitor.FindUnhealthy：判定全臂（纯函数直测） ----

    [Fact]
    public void FindUnhealthy_AllArms()
    {
        static V1Pod Pod(string name, string? ns, string? waitingReason, int restarts) => new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            Status = new V1PodStatus
            {
                ContainerStatuses = waitingReason is null && restarts == 0
                    ? null
                    : new List<V1ContainerStatus>
                    {
                        new()
                        {
                            Name = "c", Ready = false, RestartCount = restarts,
                            State = waitingReason is null
                                ? new V1ContainerState { Running = new V1ContainerStateRunning() }
                                : new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = waitingReason } },
                        },
                    },
            },
        };

        var result = K8sMonitor.FindUnhealthy(new[]
        {
            Pod("pull", "ns1", "ImagePullBackOff", 0), // ImagePullBackOff 臂
            Pod("evicted", null, "Evicted", 6), // 非 BackOff waiting + 重启激增 → restarts 臂；namespace null → ""
            Pod("calm", "ns2", "Evicted", 1), // 非 BackOff + 重启未达阈值 → 不标记
            Pod("nostatus", "ns2", null, 0), // 无 status → 不标记
        });
        Assert.Equal(2, result.Count);
        Assert.Contains(("ns1", "pull", "ImagePullBackOff"), result);
        Assert.Contains(("", "evicted", "restarts=6"), result);
    }
}
