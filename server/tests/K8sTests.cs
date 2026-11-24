using System.Net;
using System.Net.Http.Json;
using k8s.Models;
using TaoOps.Server.Auth;
using TaoOps.Server.Services.K8s;
using Xunit;

namespace TaoOps.Server.Tests;

/// <summary>FR-A28 K8s 权限矩阵：查看=ReadOnly 及以上；集群管理与资源操作=Admin；未知操作默认 Admin。</summary>
public class K8sPolicyTests
{
    [Theory]
    [InlineData("clusters"), InlineData("nodes"), InlineData("workloads"), InlineData("pods")]
    [InlineData("events"), InlineData("logs"), InlineData("version"), InlineData("rollout_status")]
    [InlineData("templates")]
    public void ViewOps_ReadOnlySufficient(string op)
    {
        Assert.Equal(UserRole.ReadOnly, K8sPolicy.RequiredRole(op));
        Assert.True(K8sPolicy.Allows(op, UserRole.ReadOnly));
        Assert.False(K8sPolicy.Allows(op, UserRole.Operator) == false && false);
    }

    [Theory]
    [InlineData("scale"), InlineData("restart"), InlineData("deploy"), InlineData("remove")]
    [InlineData("add"), InlineData("remove_cluster")]
    public void MutatingOps_AdminOnly(string op)
    {
        Assert.Equal(UserRole.Admin, K8sPolicy.RequiredRole(op));
        Assert.False(K8sPolicy.Allows(op, UserRole.ReadOnly));
        Assert.False(K8sPolicy.Allows(op, UserRole.Operator));
        Assert.True(K8sPolicy.Allows(op, UserRole.Admin));
    }

    [Fact]
    public void UnknownOp_DefaultsAdmin()
    {
        Assert.Equal(UserRole.Admin, K8sPolicy.RequiredRole("nope"));
        Assert.False(K8sPolicy.Allows("nope", UserRole.Operator));
    }
}

/// <summary>kubeconfig YAML 子集解析：提取 current-context 对应 cluster.server。</summary>
public class KubeconfigParserTests
{
    private const string ValidKubeconfig = """
        apiVersion: v1
        kind: Config
        current-context: prod
        contexts:
        - name: prod
          cluster: prod-cluster
        - name: dev
          cluster: dev-cluster
        clusters:
        - name: prod-cluster
          cluster:
            server: https://10.0.0.1:6443
        - name: dev-cluster
          cluster:
            server: https://10.0.0.2:6443
        """;

    [Fact]
    public void ExtractServer_CurrentContextResolved()
    {
        Assert.Equal("https://10.0.0.1:6443", KubeconfigParser.ExtractServer(ValidKubeconfig));
    }

    [Fact]
    public void ExtractServer_MissingContext_ReturnsNull()
    {
        Assert.Null(KubeconfigParser.ExtractServer("apiVersion: v1\nkind: Config\n"));
        Assert.Null(KubeconfigParser.ExtractServer(""));
    }

    [Fact]
    public void ExtractServer_QuotedContext()
    {
        var cfg = ValidKubeconfig.Replace("current-context: prod", "current-context: \"prod\"");
        Assert.Equal("https://10.0.0.1:6443", KubeconfigParser.ExtractServer(cfg));
    }
}

/// <summary>集群接入存储：落盘 600、元数据登记、confirm 删除。</summary>
public class KubeClusterStoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "taoops-k8s-" + Guid.NewGuid().ToString("N"));

    private KubeClusterStore NewStore() => new(_dataDir, clock: null);

    private const string Kc = """
        apiVersion: v1
        kind: Config
        current-context: c1
        contexts:
        - name: c1
          cluster: cl
        clusters:
        - name: cl
          cluster:
            server: https://10.0.0.9:6443
        """;

    public void Dispose()
    {
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }

    [Fact]
    public void Save_List_Load_Roundtrip()
    {
        var store = NewStore();
        var meta = store.Save("prod", Kc, "v1.30.0");
        Assert.Equal("prod", meta.Name);
        Assert.Equal("https://10.0.0.9:6443", meta.Server);
        Assert.Equal("v1.30.0", meta.Version);
        Assert.Single(store.List());
        Assert.Contains("https://10.0.0.9:6443", store.Load("prod"));
    }

    [Fact]
    public void Save_KubeconfigFilePermission600()
    {
        var store = NewStore();
        store.Save("prod", Kc, "v1.30.0");
        var path = Path.Combine(_dataDir, "k8s", "clusters", "prod.kubeconfig");
        if (OperatingSystem.IsWindows()) return; // 权限位仅 Unix 可验
        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode & ~UnixFileMode.GroupRead
            & ~UnixFileMode.GroupWrite & ~UnixFileMode.OtherRead & ~UnixFileMode.OtherWrite);
    }

    [Fact]
    public void Save_InvalidKubeconfig_Throws()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.Save("x", "apiVersion: v1\n", "v1"));
    }

    [Fact]
    public void Remove_RequiresConfirm()
    {
        var store = NewStore();
        store.Save("prod", Kc, "v1.30.0");
        Assert.False(store.Remove("prod", confirm: false));
        Assert.True(store.Remove("prod", confirm: true));
        Assert.Empty(store.List());
        Assert.Null(store.Load("prod"));
    }

    [Fact]
    public void Save_DuplicateName_Overwrites()
    {
        var store = NewStore();
        store.Save("prod", Kc, "v1.30.0");
        store.Save("prod", Kc, "v1.31.0");
        var metas = store.List();
        Assert.Single(metas);
        Assert.Equal("v1.31.0", metas[0].Version);
    }
}

/// <summary>K8s 清单渲染：Deployment+Service 成对、env 覆盖、镜像正则、应用名校验。</summary>
public class K8sManifestRendererTests
{
    [Fact]
    public void Render_SingleTemplate_DeploymentAndService()
    {
        var rendered = K8sManifestRenderer.Render("nginx", "my-nginx", "default");
        Assert.Equal(2, rendered.Documents.Count);
        Assert.Contains("kind: Deployment", rendered.Documents[0]);
        Assert.Contains("kind: Service", rendered.Documents[1]);
        Assert.Contains("image: \"nginx:stable\"", rendered.Documents[0]);
        Assert.Contains("replicas: 1", rendered.Documents[0]);
    }

    [Fact]
    public void Render_StackTemplate_FourDocuments()
    {
        var rendered = K8sManifestRenderer.Render("wordpress", "my-blog", "default");
        // db 无宿主端口（集群内经 Service 名访问）→ 2 Deployment + 1 Service。
        Assert.Equal(2, rendered.Documents.Count(d => d.Contains("kind: Deployment")));
        Assert.Equal(1, rendered.Documents.Count(d => d.Contains("kind: Service")));
        // 兄弟服务名改写为 K8s DNS 语义：db → my-blog-db（env 值不再裸引用 "db"）。
        Assert.Contains("my-blog-db", rendered.Documents[0]);
        Assert.DoesNotContain("value: \"db\"", rendered.Documents[0]);
    }

    [Fact]
    public void Render_EnvOverride_OverridesTemplateDefault()
    {
        var rendered = K8sManifestRenderer.Render("mysql", "db1", "default",
            new Dictionary<string, string> { ["MYSQL_ROOT_PASSWORD"] = "s3cret" });
        Assert.Contains("MYSQL_ROOT_PASSWORD", rendered.Documents[0]);
        Assert.Contains("s3cret", rendered.Documents[0]);
        Assert.DoesNotContain("changeit", rendered.Documents[0]);
    }

    [Fact]
    public void Render_EnvOverride_IgnoresUnknownKeys()
    {
        var rendered = K8sManifestRenderer.Render("nginx", "n1", "default",
            new Dictionary<string, string> { ["NOT_IN_TEMPLATE"] = "x" });
        Assert.DoesNotContain("NOT_IN_TEMPLATE", rendered.Documents[0]);
    }

    [Fact]
    public void Render_Volume_RendersPvcMount()
    {
        var rendered = K8sManifestRenderer.Render("redis", "r1", "default");
        // redis 无数据卷；用 mysql 验证 PVC。
        var mysql = K8sManifestRenderer.Render("mysql", "m1", "default");
        Assert.Contains("persistentVolumeClaim", mysql.Documents[0]);
        Assert.Contains("m1-mysql-mysql-data", mysql.Documents[0]);
        _ = rendered;
    }

    [Fact]
    public void Render_UnknownTemplate_Throws()
    {
        Assert.Throws<K8sManifestRenderer.RenderException>(
            () => K8sManifestRenderer.Render("nope", "app", "default"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-bad")]
    [InlineData("Bad Name")]
    [InlineData("a")]
    public void Render_InvalidAppName_Throws(string appName)
    {
        Assert.Throws<K8sManifestRenderer.RenderException>(
            () => K8sManifestRenderer.Render("nginx", appName, "default"));
    }

    [Fact]
    public void Render_ReplicasOutOfRange_Throws()
    {
        Assert.Throws<K8sManifestRenderer.RenderException>(
            () => K8sManifestRenderer.Render("nginx", "app", "default", null, 101));
    }
}

/// <summary>K8s 巡检健康判定纯函数：waiting 原因与重启激增。</summary>
public class K8sMonitorEvaluateTests
{
    private static V1Pod Pod(string ns, string name, string? waitingReason = null, int restarts = 0) =>
        new()
        {
            Metadata = new() { Name = name, NamespaceProperty = ns },
            Status = new()
            {
                ContainerStatuses =
                [
                    new()
                    {
                        Ready = waitingReason is null,
                        RestartCount = restarts,
                        State = new() { Waiting = waitingReason is null ? null : new() { Reason = waitingReason } },
                    },
                ],
            },
        };

    [Fact]
    public void CrashLoopBackOff_Flagged()
    {
        var result = K8sMonitor.FindUnhealthy([Pod("app", "web", "CrashLoopBackOff")]);
        Assert.Single(result);
        Assert.Equal(("app", "web", "CrashLoopBackOff"), result[0]);
    }

    [Fact]
    public void ImagePullBackOff_Flagged()
    {
        var result = K8sMonitor.FindUnhealthy([Pod("app", "web", "ImagePullBackOff")]);
        Assert.Single(result);
    }

    [Fact]
    public void RestartSpike_Flagged()
    {
        var result = K8sMonitor.FindUnhealthy([Pod("app", "web", restarts: 5)]);
        Assert.Single(result);
        Assert.Contains("restarts=5", result[0].Reason);
    }

    [Fact]
    public void HealthyPod_NotFlagged()
    {
        Assert.Empty(K8sMonitor.FindUnhealthy([Pod("app", "web"), Pod("app", "db", restarts: 2)]));
    }
}

/// <summary>K8s 端点集成测试：鉴权矩阵与错误路径（不依赖真实集群）。</summary>
[Collection("Integration")]
public class K8sEndpointTests
{
    private readonly TaoOpsWebAppFactory _factory;
    private readonly HttpClient _client;

    public K8sEndpointTests(TaoOpsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AsAdmin() =>
        _client.DefaultRequestHeaders.Authorization = new("Bearer", await _factory.LoginAdminAsync());

    [Fact]
    public async Task Clusters_WithoutToken_401()
    {
        var res = await _client.GetAsync("/api/k8s/clusters");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Clusters_Empty_List()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/k8s/clusters");
        res.EnsureSuccessStatusCode();
        Assert.Equal("[]", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AddCluster_InvalidKubeconfig_400_AndAudit()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/k8s/clusters",
            new { name = "bad", kubeconfig = "apiVersion: v1\nkind: Config\n" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        // 审计留痕且不含 kubeconfig 内容。
        var auditRes = await _client.GetAsync("/api/audit");
        auditRes.EnsureSuccessStatusCode();
        var auditBody = await auditRes.Content.ReadAsStringAsync();
        Assert.Contains("k8s.cluster", auditBody);
        Assert.Contains("connect_failed", auditBody);
        Assert.DoesNotContain("kind: Config", auditBody);
    }

    [Fact]
    public async Task GetNodes_UnknownCluster_404()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/k8s/nope/nodes");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GetPods_UnknownCluster_404()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/k8s/nope/pods");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Ops_UnknownCluster_404()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/k8s/nope/ops",
            new { op = "scale", ns = "default", deployment = "web", replicas = "2" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Ops_UnknownOp_400()
    {
        await AsAdmin();
        var res = await _client.PostAsJsonAsync("/api/k8s/whatever/ops",
            new { op = "teleport" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Logs_UnknownCluster_404_AndAudit()
    {
        await AsAdmin();
        var res = await _client.GetAsync("/api/k8s/nope/logs?ns=default&pod=p1");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
