using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using TaoOps.Server.Services.K8s;

namespace TaoOps.Server.Tests;

/// <summary>
/// 假 K8s API 服务器：进程内 Kestrel 随机端口，按（方法, 路径）路由 canned 响应，
/// 支持排队（409 冲突转替换）。kubeconfig 直指本服务器，走真实客户端工厂。
/// </summary>
public sealed class FakeK8sApiServer : IAsyncDisposable
{
    private readonly Dictionary<string, Queue<Func<(int Status, string Body, string ContentType)>>> _routes = new(StringComparer.Ordinal);
    private WebApplication? _app;

    /// <summary>请求留痕（方法+路径），供断言确认真实调用路径。</summary>
    public readonly List<string> Requests = new();

    public string BaseUrl { get; private set; } = "";

    public string Kubeconfig => $$"""
        apiVersion: v1
        kind: Config
        current-context: c1
        contexts:
        - name: c1
          context:
            cluster: cl
        clusters:
        - name: cl
          cluster:
            server: {{BaseUrl}}
        """;

    public void Reset() => _routes.Clear();

    /// <summary>登记响应；同路径多次登记按序出队（如先 409 后成功）。</summary>
    public void On(string method, string path, params (int Status, string Body, string ContentType)[] responses)
    {
        var q = _routes.TryGetValue($"{method} {path}", out var existing)
            ? existing : _routes[$"{method} {path}"] = new();
        foreach (var r in responses) q.Enqueue(() => r);
    }

    public void OnJson(string method, string path, string json, int status = 200) =>
        On(method, path, (status, json, "application/json"));

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async ctx =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.Length > 1) path = path.TrimEnd('/'); // k8s 客户端部分请求带尾斜杠
            Requests.Add($"{ctx.Request.Method} {path}");
            if (_routes.TryGetValue($"{ctx.Request.Method} {path}", out var q) && q.Count > 0)
            {
                var (status, body, contentType) = q.Dequeue()();
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                await ctx.Response.WriteAsync(body);
                return;
            }
            // 未路由的列表调用兜底空集合，避免无关 502 干扰断言。
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"items":[]}""");
        });
        await app.StartAsync();
        _app = app;
        BaseUrl = app.Urls.First(u => u.StartsWith("http://127.0.0.1", StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is { } a) await a.StopAsync();
    }
}

/// <summary>
/// 批次三专用夹具：装配同集成夹具（共享 TAOOPS_DATA_DIR），但停掉托管巡检
/// （避免后台扫描与直测 ScanOnce 抢边缘触发）。K8s 客户端走真实工厂连假 API 服务器。
/// </summary>
public sealed class K8sFakeApiFactory : WebApplicationFactory<Program>
{
    public FakeK8sApiServer Api { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());

    /// <summary>对本机 host 登录（TokenStore 按主机实例隔离，须用本厂 client）；账号库为空时先引导。</summary>
    public async Task<string> LoginAdminAsync()
    {
        using var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login",
            new { username = TaoOpsWebAppFactory.AdminUser, password = TaoOpsWebAppFactory.AdminPassword });
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            await client.PostAsJsonAsync("/api/auth/bootstrap",
                new { username = TaoOpsWebAppFactory.AdminUser, password = TaoOpsWebAppFactory.AdminPassword });
            res = await client.PostAsJsonAsync("/api/auth/login",
                new { username = TaoOpsWebAppFactory.AdminUser, password = TaoOpsWebAppFactory.AdminPassword });
        }
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }
}

/// <summary>
/// 分支账批次三：K8sClusterService 成功路径 + 降级/冲突分支 + K8sMonitor 边缘触发，
/// 经假 K8s API 服务器走真实端点管道（不依赖真实集群）。
/// </summary>
[Collection("Integration")]
public class K8sServiceBackfillTests : IDisposable
{
    private const string DeploymentJson = """
        {"metadata":{"name":"web","namespace":"default"},
         "spec":{"replicas":3,"template":{"spec":{"containers":[{"name":"app","image":"nginx:1.0"}]}}},
         "status":{"replicas":3,"updatedReplicas":3,"availableReplicas":3,"readyReplicas":3}}
        """;

    /// <summary>DELETE 端点标准返回 V1Status（status 为字符串字段）。</summary>
    private const string StatusJson = """
        {"kind":"Status","apiVersion":"v1","metadata":{},"status":"Success"}
        """;

    private readonly K8sFakeApiFactory _factory;
    private readonly HttpClient _client;
    private readonly string _dataDir;

    public K8sServiceBackfillTests()
    {
        _factory = new K8sFakeApiFactory();
        _factory.Api.StartAsync().GetAwaiter().GetResult();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", _factory.LoginAdminAsync().GetAwaiter().GetResult());
        _dataDir = Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR")!;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _factory.Api.DisposeAsync().GetAwaiter().GetResult();
    }

    private void SeedCluster(string name) =>
        new KubeClusterStore(_dataDir).Save(name, _factory.Api.Kubeconfig, "v1.29.0");

    /// <summary>不可达集群（连接拒绝），供降级/静默分支。</summary>
    private void SeedDeadCluster(string name) =>
        new KubeClusterStore(_dataDir).Save(name,
            """
            apiVersion: v1
            kind: Config
            current-context: c1
            contexts:
            - name: c1
              context:
                cluster: cl
            clusters:
            - name: cl
              cluster:
                server: http://127.0.0.1:1
            """, "v1.29.0");

    // ---- 集群接入：连接测试成功路径 ----

    [Fact]
    public async Task AddCluster_VersionProbe_Succeeds_AndLists()
    {
        _factory.Api.OnJson("GET", "/version", """{"major":"1","minor":"29","gitVersion":"v1.29.0"}""");
        var res = await _client.PostAsJsonAsync("/api/k8s/clusters", new { name = "probe-ok", kubeconfig = _factory.Api.Kubeconfig });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("version").GetString() == "v1.29.0",
            $"version body: {body.GetRawText()} | requests: {string.Join(" | ", _factory.Api.Requests)}");

        var list = await _client.GetAsync("/api/k8s/clusters");
        Assert.Contains("probe-ok", await list.Content.ReadAsStringAsync());
        Assert.Contains("GET /version", _factory.Api.Requests);
    }

    // ---- 只读层：节点/工作负载/Pod/事件/日志 ----

    [Fact]
    public async Task Nodes_List_MapsReadyAndCapacity()
    {
        SeedCluster("nodes-1");
        _factory.Api.OnJson("GET", "/api/v1/nodes", """
            {"items":[
              {"metadata":{"name":"n1"},"status":{"conditions":[{"type":"Ready","status":"True"}],
               "capacity":{"cpu":"4","memory":"8Gi"},"nodeInfo":{"kubeletVersion":"v1.29.0"}}},
              {"metadata":{"name":"n2"},"status":{"conditions":[{"type":"Ready","status":"False"}],
               "capacity":{"cpu":"2","memory":"4Gi"}}}
            ]}
            """);
        var res = await _client.GetAsync("/api/k8s/nodes-1/nodes");
        Assert.True(res.IsSuccessStatusCode, $"nodes failed: {res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("Ready", items[0].GetProperty("status").GetString());
        Assert.Equal(4, items[0].GetProperty("cpuCores").GetDouble());
        Assert.Equal("8Gi", items[0].GetProperty("memory").GetString());
        Assert.Equal("NotReady", items[1].GetProperty("status").GetString());
        Assert.Equal("", items[1].GetProperty("version").GetString()); // 无 nodeInfo 兜底空串
    }

    [Fact]
    public async Task Workloads_ListsThreeKinds_AllAndNamespaced()
    {
        SeedCluster("wl-1");
        _factory.Api.OnJson("GET", "/apis/apps/v1/deployments", """
            {"items":[{"metadata":{"name":"web","namespace":"default","creationTimestamp":"2026-11-20T00:00:00Z"},
              "spec":{"replicas":3,"template":{"spec":{"containers":[{"name":"app","image":"nginx:1.0"}]}}},
              "status":{"readyReplicas":2}}]}
            """);
        _factory.Api.OnJson("GET", "/apis/apps/v1/statefulsets", """
            {"items":[{"metadata":{"name":"db","namespace":"data","creationTimestamp":"2026-11-20T00:00:00Z"},
              "spec":{"replicas":1,"template":{"spec":{"containers":[{"name":"db","image":"pg:16"}]}}},
              "status":{"readyReplicas":1}}]}
            """);
        _factory.Api.OnJson("GET", "/apis/apps/v1/daemonsets", """
            {"items":[{"metadata":{"name":"agent","namespace":"kube-system","creationTimestamp":"2026-11-20T00:00:00Z"},
              "spec":{"template":{"spec":{"containers":[{"name":"agent","image":"agent:1"}]}}},
              "status":{"numberReady":3,"desiredNumberScheduled":3}}]}
            """);
        var all = await _client.GetAsync("/api/k8s/wl-1/workloads");
        all.EnsureSuccessStatusCode();
        var kinds = (await all.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(x => x.GetProperty("kind").GetString()!).ToHashSet();
        Assert.Equal(new HashSet<string> { "Deployment", "StatefulSet", "DaemonSet" }, kinds);

        // 指定命名空间：走 namespaced 路径。
        _factory.Api.Reset();
        _factory.Api.OnJson("GET", "/apis/apps/v1/namespaces/default/deployments", """{"items":[]}""");
        _factory.Api.OnJson("GET", "/apis/apps/v1/namespaces/default/statefulsets", """{"items":[]}""");
        _factory.Api.OnJson("GET", "/apis/apps/v1/namespaces/default/daemonsets", """{"items":[]}""");
        var ns = await _client.GetAsync("/api/k8s/wl-1/workloads?ns=default");
        ns.EnsureSuccessStatusCode();
        Assert.Contains("GET /apis/apps/v1/namespaces/default/deployments", _factory.Api.Requests);
    }

    [Fact]
    public async Task Pods_List_AggregatesNamespaces_AndMapsStatus()
    {
        SeedCluster("pods-1");
        _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}},{"metadata":{"name":"kube-system"}}]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """
            {"items":[
              {"metadata":{"name":"web-1","namespace":"default","creationTimestamp":"2026-11-20T00:00:00Z"},
               "spec":{"nodeName":"n1"},
               "status":{"phase":"Running","containerStatuses":[
                 {"name":"app","ready":true,"restartCount":1,"state":{"running":{}}},
                 {"name":"side","ready":true,"restartCount":2,"state":{"running":{}}}]}},
              {"metadata":{"name":"bad-1","namespace":"default"},
               "spec":{"nodeName":"n1"},
               "status":{"phase":"Running","containerStatuses":[
                 {"name":"app","ready":false,"restartCount":0,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}}
            ]}
            """);
        _factory.Api.OnJson("GET", "/api/v1/namespaces/kube-system/pods", """{"items":[]}""");
        var res = await _client.GetAsync("/api/k8s/pods-1/pods");
        res.EnsureSuccessStatusCode();
        var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(3, items[0].GetProperty("restarts").GetInt32()); // 跨容器求和
        Assert.True(items[0].GetProperty("ready").GetBoolean());
        Assert.Equal("CrashLoopBackOff", items[1].GetProperty("reason").GetString());
        Assert.False(items[1].GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Events_List_AllAndNamespaced()
    {
        SeedCluster("ev-1");
        _factory.Api.OnJson("GET", "/api/v1/events", """
            {"items":[
              {"metadata":{"namespace":"default","creationTimestamp":"2026-11-20T00:00:00Z"},
               "involvedObject":{"name":"web"},"type":"Warning","reason":"BackOff","message":"backoff loop"},
              {"metadata":{"namespace":"default","creationTimestamp":"2026-11-21T00:00:00Z"},
               "involvedObject":{"name":"db"},"type":"Normal","reason":"Started","message":"started"}
            ]}
            """);
        var all = await _client.GetAsync("/api/k8s/ev-1/events");
        all.EnsureSuccessStatusCode();
        var items = (await all.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("Started", items[0].GetProperty("reason").GetString()); // 按时间倒序

        _factory.Api.Reset();
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/events", """{"items":[]}""");
        var ns = await _client.GetAsync("/api/k8s/ev-1/events?ns=default");
        ns.EnsureSuccessStatusCode();
        Assert.Contains("GET /api/v1/namespaces/default/events", _factory.Api.Requests);
    }

    [Fact]
    public async Task Logs_ReturnsContent_AndAudits()
    {
        SeedCluster("log-1");
        _factory.Api.On("GET", "/api/v1/namespaces/default/pods/web-1/log", (200, "hello log", "text/plain"));
        var res = await _client.GetAsync("/api/k8s/log-1/logs?ns=default&pod=web-1");
        res.EnsureSuccessStatusCode();
        Assert.Equal("hello log", await res.Content.ReadAsStringAsync());
        var audit = await _client.GetAsync("/api/audit");
        Assert.Contains("k8s.logs", await audit.Content.ReadAsStringAsync());
    }

    // ---- 指标：metrics-server 在与不在两条分支 ----

    [Fact]
    public async Task Metrics_WithServer_ReturnsUsage()
    {
        SeedCluster("met-1");
        _factory.Api.OnJson("GET", "/apis/metrics.k8s.io/v1beta1/pods", """
            {"items":[{"metadata":{"name":"web-1","namespace":"default"},
              "containers":[{"name":"app","usage":{"cpu":"250m","memory":"512Mi"}}]}]}
            """);
        var res = await _client.GetAsync("/api/k8s/met-1/metrics");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.Equal(250, body.GetProperty("pods")[0].GetProperty("cpuMilli").GetInt64());
    }

    [Fact]
    public async Task Metrics_WithoutServer_DegradesWithNote()
    {
        SeedCluster("met-2");
        _factory.Api.OnJson("GET", "/apis/metrics.k8s.io/v1beta1/pods",
            """{"kind":"Status","reason":"NotFound"}""", status: 404);
        var res = await _client.GetAsync("/api/k8s/met-2/metrics");
        res.EnsureSuccessStatusCode(); // 降级不报错
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.True(body.GetProperty("note").GetString()!.Length > 0);
    }

    // ---- 操作层：scale/restart/rollout/deploy/remove ----

    [Fact]
    public async Task Ops_Scale_PatchesScaleSubresource()
    {
        SeedCluster("ops-1");
        _factory.Api.OnJson("PATCH", "/apis/apps/v1/namespaces/default/deployments/web/scale", DeploymentJson);
        var res = await _client.PostAsJsonAsync("/api/k8s/ops-1/ops",
            new { op = "scale", ns = "default", deployment = "web", replicas = "2" });
        Assert.True(res.IsSuccessStatusCode, $"scale failed: {res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("desired").GetInt32());
        Assert.Contains("PATCH /apis/apps/v1/namespaces/default/deployments/web/scale", _factory.Api.Requests);
    }

    [Fact]
    public async Task Ops_Restart_PatchesDeploymentTemplate()
    {
        SeedCluster("ops-2");
        _factory.Api.OnJson("PATCH", "/apis/apps/v1/namespaces/default/deployments/web", DeploymentJson);
        var res = await _client.PostAsJsonAsync("/api/k8s/ops-2/ops",
            new { op = "restart", ns = "default", deployment = "web" });
        Assert.True(res.IsSuccessStatusCode, $"restart failed: {res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        Assert.Contains("PATCH /apis/apps/v1/namespaces/default/deployments/web", _factory.Api.Requests);
    }

    [Fact]
    public async Task Ops_RolloutStatus_CompleteAndInProgress()
    {
        SeedCluster("ops-3");
        _factory.Api.OnJson("GET", "/apis/apps/v1/namespaces/default/deployments/web/status", DeploymentJson);
        var done = await _client.PostAsJsonAsync("/api/k8s/ops-3/ops",
            new { op = "rollout_status", ns = "default", deployment = "web" });
        done.EnsureSuccessStatusCode();
        var body = await done.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("complete").GetBoolean());

        _factory.Api.Reset();
        _factory.Api.OnJson("GET", "/apis/apps/v1/namespaces/default/deployments/web/status", """
            {"metadata":{"name":"web","namespace":"default"},
             "spec":{"replicas":3},"status":{"updatedReplicas":1,"availableReplicas":1}}
            """);
        var wip = await _client.PostAsJsonAsync("/api/k8s/ops-3/ops",
            new { op = "rollout_status", ns = "default", deployment = "web" });
        wip.EnsureSuccessStatusCode();
        var body2 = await wip.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body2.GetProperty("complete").GetBoolean());
        Assert.Contains("in progress", body2.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Ops_Deploy_CreatesAndReplacesOnConflict()
    {
        SeedCluster("ops-4");
        // 首次部署：Deployment+Service 各建一次。
        _factory.Api.OnJson("POST", "/apis/apps/v1/namespaces/default/deployments", DeploymentJson);
        _factory.Api.OnJson("POST", "/api/v1/namespaces/default/services", """
            {"metadata":{"name":"nginx","namespace":"default"},"spec":{"ports":[]}}
            """);
        var res = await _client.PostAsJsonAsync("/api/k8s/ops-4/ops",
            new { op = "deploy", template = "nginx", name = "nginx", ns = "default" });
        Assert.True(res.IsSuccessStatusCode, $"deploy failed: {res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("deployments").GetInt32());
        Assert.Equal(1, body.GetProperty("services").GetInt32());

        // 重复部署：创建 409 → 转替换。
        _factory.Api.Reset();
        _factory.Api.OnJson("POST", "/apis/apps/v1/namespaces/default/deployments",
            """{"kind":"Status","reason":"Conflict"}""", status: 409);
        _factory.Api.OnJson("PUT", "/apis/apps/v1/namespaces/default/deployments/nginx2-nginx", DeploymentJson);
        _factory.Api.OnJson("POST", "/api/v1/namespaces/default/services", """
            {"metadata":{"name":"nginx","namespace":"default"},"spec":{"ports":[]}}
            """);
        var again = await _client.PostAsJsonAsync("/api/k8s/ops-4/ops",
            new { op = "deploy", template = "nginx", name = "nginx2", ns = "default" });
        again.EnsureSuccessStatusCode();
        Assert.True(_factory.Api.Requests.Any(r => r.StartsWith("PUT /apis/", StringComparison.Ordinal)),
            "requests: " + string.Join(" | ", _factory.Api.Requests));
    }

    [Fact]
    public async Task Ops_Remove_ConfirmGate_AndDeletes()
    {
        SeedCluster("ops-5");
        var noConfirm = await _client.PostAsJsonAsync("/api/k8s/ops-5/ops",
            new { op = "remove", name = "nginx", ns = "default" });
        Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode); // 无 confirm → 400

        _factory.Api.OnJson("GET", "/apis/apps/v1/namespaces/default/deployments", """
            {"items":[{"metadata":{"name":"nginx-dep","namespace":"default"}}]}
            """);
        _factory.Api.OnJson("DELETE", "/apis/apps/v1/namespaces/default/deployments/nginx-dep",
            StatusJson);
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/services", """
            {"items":[{"metadata":{"name":"nginx-svc","namespace":"default"}}]}
            """);
        // 客户端库不一致：DeleteNamespacedDeployment 返回 V1Status，DeleteNamespacedService 返回 V1Service。
        _factory.Api.OnJson("DELETE", "/api/v1/namespaces/default/services/nginx-svc", """
            {"metadata":{"name":"nginx-svc","namespace":"default"},"spec":{"ports":[]}}
            """);
        var ok = await _client.PostAsJsonAsync("/api/k8s/ops-5/ops",
            new { op = "remove", name = "nginx", ns = "default", confirm = true });
        Assert.True(ok.IsSuccessStatusCode, $"remove failed: {ok.StatusCode} {await ok.Content.ReadAsStringAsync()}");
        var summary = await ok.Content.ReadAsStringAsync();
        Assert.Contains("deployments=1", summary);
        Assert.Contains("services=1", summary);
    }

    // ---- 健康总览：正常汇总 + 单集群失败降级 ----

    [Fact]
    public async Task Health_SumsClusters_AndDegradesOnError()
    {
        SeedCluster("health-1");
        SeedDeadCluster("health-2");
        _factory.Api.OnJson("GET", "/api/v1/nodes", """
            {"items":[{"metadata":{"name":"n1"},"status":{"conditions":[{"type":"Ready","status":"True"}],"capacity":{"cpu":"4","memory":"8Gi"}}}]}
            """);
        _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}}]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """{"items":[]}""");
        var res = await _client.GetAsync("/api/k8s/health");
        res.EnsureSuccessStatusCode();
        // 数据目录为集合共享，其它用例的集群也在列；只看本用例两个集群。
        var items = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Where(x => x.GetProperty("cluster").GetString() is "health-1" or "health-2").ToList();
        Assert.Equal(2, items.Count);
        var ok = items.Single(x => x.GetProperty("cluster").GetString() == "health-1");
        Assert.Equal(1, ok.GetProperty("nodesTotal").GetInt32());
        Assert.Equal(JsonValueKind.Null, ok.GetProperty("error").ValueKind);

        // health-2 连接拒绝 → 降级为 Error 说明，不影响其它集群。
        var bad = items.Single(x => x.GetProperty("cluster").GetString() == "health-2");
        Assert.True(bad.GetProperty("error").GetString()!.Length > 0);
    }

    // ---- K8sMonitor：边缘触发、恢复复位、失败静默 ----

    [Fact]
    public void Monitor_ScanOnce_EdgeTrigger_Reset_Retigger()
    {
        SeedCluster("mon-1");
        _factory.Api.Reset();
        _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}}]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """
            {"items":[{"metadata":{"name":"bad-1","namespace":"default"},
              "status":{"phase":"Running","containerStatuses":[
                {"name":"app","ready":false,"restartCount":0,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}}]}
            """);
        var monitor = _factory.Services.GetRequiredService<K8sMonitor>();
        Assert.Equal(1, monitor.ScanOnce()); // 首次触发
        Assert.Equal(0, monitor.ScanOnce()); // 同一 Pod 不重复告警

        // 恢复：Pod 变健康 → 清边缘标记；再坏 → 重新触发。
        _factory.Api.Reset();
        _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}}]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """{"items":[]}""");
        Assert.Equal(0, monitor.ScanOnce());
        _factory.Api.Reset();
        _factory.Api.OnJson("GET", "/api/v1/namespaces", """{"items":[{"metadata":{"name":"default"}}]}""");
        _factory.Api.OnJson("GET", "/api/v1/namespaces/default/pods", """
            {"items":[{"metadata":{"name":"bad-2","namespace":"default"},
              "status":{"phase":"Running","containerStatuses":[
                {"name":"app","ready":false,"restartCount":7,"state":{"running":{}}}]}}]}
            """);
        Assert.Equal(1, monitor.ScanOnce()); // 重启激增（≥5）触发
    }

    [Fact]
    public void Monitor_ScanOnce_FactoryFails_Silent()
    {
        SeedDeadCluster("mon-2");
        var monitor = _factory.Services.GetRequiredService<K8sMonitor>();
        Assert.Equal(0, monitor.ScanOnce()); // 巡检失败静默，不抛不告警
    }
}
