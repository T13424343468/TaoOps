using k8s;
using System.Text.Json;
using k8s.KubeConfigModels;
using k8s.Models;

namespace TaoOps.Server.Services.K8s;

/// <summary>客户端工厂：从 kubeconfig 原文创建 IKubernetes（测试可替换）。</summary>
public interface IK8sClientFactory
{
    IKubernetes Create(string kubeconfig);
}

/// <summary>默认工厂：KubernetesYaml 解析 → BuildConfigFromConfigObject → new Kubernetes。</summary>
public sealed class K8sClientFactory : IK8sClientFactory
{
    public IKubernetes Create(string kubeconfig)
    {
        var config = KubernetesYaml.Deserialize<K8SConfiguration>(kubeconfig)
            ?? throw new ArgumentException("kubeconfig 解析失败");
        var cfg = KubernetesClientConfiguration.BuildConfigFromConfigObject(config);
        return new Kubernetes(cfg);
    }
}

/// <summary>
/// FR-A28 集群操作服务：只读层（节点/工作负载/Pod/事件/日志）、操作层
/// （扩缩容/重启/滚动更新进度）、部署层（模板渲染清单直发集群）。
/// kubeconfig 经 KubeClusterStore 读取，凭据内容不进审计与日志。
/// </summary>
public sealed class K8sClusterService(KubeClusterStore store, IK8sClientFactory factory)
{
    public sealed record NodeSummary(string Name, string Status, string Version,
        double CpuCores, string Memory, int Pods);
    public sealed record WorkloadSummary(string Kind, string Name, string Namespace,
        int Ready, int Desired, string? Image, long? AgeMs);
    public sealed record PodSummary(string Name, string Namespace, string Node,
        string Phase, int Restarts, bool Ready, long? AgeMs, string? Reason);
    public sealed record EventSummary(string Namespace, string Type, string Reason,
        string Object, string Message, long? LastTsMs);
    public sealed record RolloutInfo(string Name, int Desired, int Updated, int Available,
        bool Complete, string? Message);
    public sealed record DeployResult(string App, string Namespace, int Deployments, int Services);

    private IKubernetes Client(string cluster) =>
        factory.Create(store.Load(cluster)
            ?? throw new KeyNotFoundException($"集群不存在: {cluster}"));

    private static long NowMs => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    private static long? AgeMs(DateTime? creation)
    {
        if (creation is not { } ts || ts == default) return null;
        return Math.Max(0, NowMs - new DateTimeOffset(ts.ToUniversalTime()).ToUnixTimeMilliseconds());
    }

    /// <summary>连接测试：version 探测（接入成功判定）。</summary>
    public string TestVersion(string kubeconfig)
    {
        using var client = factory.Create(kubeconfig);
        var version = client.Version.GetCode();
        return version.GitVersion ?? $"{version.Major}.{version.Minor}";
    }

    public IReadOnlyList<NodeSummary> ListNodes(string cluster)
    {
        using var client = Client(cluster);
        var nodes = client.CoreV1.ListNode();
        return nodes.Items.Select(n =>
        {
            var ready = n.Status.Conditions?.FirstOrDefault(c => c.Type == "Ready");
            var cpu = n.Status.Capacity?.TryGetValue("cpu", out var c) == true
                && double.TryParse(c.ToString(), out var cv) ? cv : 0;
            var mem = n.Status.Capacity?.TryGetValue("memory", out var m) == true ? m.ToString() : "";
            return new NodeSummary(n.Metadata.Name,
                ready?.Status == "True" ? "Ready" : "NotReady",
                n.Status.NodeInfo?.KubeletVersion ?? "",
                cpu, mem, 0);
        }).ToList();
    }

    public IReadOnlyList<WorkloadSummary> ListWorkloads(string cluster, string ns)
    {
        using var client = Client(cluster);
        var result = new List<WorkloadSummary>();
        var deployments = string.IsNullOrEmpty(ns)
            ? client.AppsV1.ListDeploymentForAllNamespaces()
            : client.AppsV1.ListNamespacedDeployment(ns);
        foreach (var d in deployments.Items)
            result.Add(new WorkloadSummary("Deployment", d.Metadata.Name, d.Metadata.NamespaceProperty ?? "",
                (int)(d.Status.ReadyReplicas ?? 0), (int)(d.Spec.Replicas ?? 0),
                d.Spec.Template.Spec.Containers.FirstOrDefault()?.Image, AgeMs(d.Metadata.CreationTimestamp)));
        var statefulSets = string.IsNullOrEmpty(ns)
            ? client.AppsV1.ListStatefulSetForAllNamespaces()
            : client.AppsV1.ListNamespacedStatefulSet(ns);
        foreach (var s in statefulSets.Items)
            result.Add(new WorkloadSummary("StatefulSet", s.Metadata.Name, s.Metadata.NamespaceProperty ?? "",
                (int)(s.Status.ReadyReplicas ?? 0), (int)(s.Spec.Replicas ?? 0),
                s.Spec.Template.Spec.Containers.FirstOrDefault()?.Image, AgeMs(s.Metadata.CreationTimestamp)));
        var daemonSets = string.IsNullOrEmpty(ns)
            ? client.AppsV1.ListDaemonSetForAllNamespaces()
            : client.AppsV1.ListNamespacedDaemonSet(ns);
        foreach (var ds in daemonSets.Items)
            result.Add(new WorkloadSummary("DaemonSet", ds.Metadata.Name, ds.Metadata.NamespaceProperty ?? "",
                (int)(ds.Status.NumberReady), (int)(ds.Status.DesiredNumberScheduled),
                ds.Spec.Template.Spec.Containers.FirstOrDefault()?.Image, AgeMs(ds.Metadata.CreationTimestamp)));
        return result;
    }

    public IReadOnlyList<PodSummary> ListPods(string cluster, string ns)
    {
        using var client = Client(cluster);
        // 客户端无 Pod 全命名空间列表方法：ns 为空时逐命名空间聚合。
        IEnumerable<k8s.Models.V1Pod> pods = string.IsNullOrEmpty(ns)
            ? client.CoreV1.ListNamespace().Items
                .SelectMany(n => client.CoreV1.ListNamespacedPod(n.Metadata.Name).Items)
            : client.CoreV1.ListNamespacedPod(ns).Items;
        return pods.Select(p =>
        {
            var restarts = (int)(p.Status?.ContainerStatuses?.Sum(c => c.RestartCount) ?? 0);
            var ready = p.Status?.ContainerStatuses is { } cs && cs.Count > 0 && cs.All(c => c.Ready);
            var waiting = p.Status?.ContainerStatuses?
                .FirstOrDefault(c => c.State?.Waiting is not null)?.State?.Waiting?.Reason;
            return new PodSummary(p.Metadata.Name, p.Metadata.NamespaceProperty ?? "",
                p.Spec?.NodeName ?? "", p.Status?.Phase ?? "Unknown", restarts, ready,
                AgeMs(p.Metadata.CreationTimestamp), waiting);
        }).ToList();
    }

    /// <summary>Pod 资源用量（backlog C1）：metrics.k8s.io 自定义 API，未装 metrics-server 时降级。</summary>
    public sealed record ContainerMetric(string Name, long CpuMilli, double MemMiB);
    public sealed record PodMetricSummary(string Namespace, string Name, string Node,
        long CpuMilli, double MemMiB, IReadOnlyList<ContainerMetric> Containers);
    public sealed record PodMetricsResult(bool Available, string? Note, IReadOnlyList<PodMetricSummary> Pods);

    /// <summary>
    /// Pod/容器级 CPU（毫核）与内存（MiB）用量：走 metrics.k8s.io/v1beta1 自定义 API
    /// （metrics-server 提供）。集群未装 metrics-server 时 Available=false 并附说明，
    /// 降级不报错（口径同巡检降级）。
    /// </summary>
    public async Task<PodMetricsResult> PodMetricsAsync(string cluster)
    {
        using var client = Client(cluster);
        object raw;
        try
        {
            raw = await client.CustomObjects.ListClusterCustomObjectAsync("metrics.k8s.io", "v1beta1", "pods");
        }
        catch (Exception ex) when (ex.Message.Contains("404") || ex.Message.Contains("NotFound")
            || ex.Message.Contains("the server could not find") || ex.Message.Contains("no such host"))
        {
            return new PodMetricsResult(false, "集群未安装 metrics-server 或指标 API 不可用，无法展示实时用量", Array.Empty<PodMetricSummary>());
        }
        return ParsePodMetrics(raw);
    }

    /// <summary>解析 PodMetricsList JSON（纯函数，测试直测）：cpu 毫核、内存 MiB，逐容器聚合。</summary>
    public static PodMetricsResult ParsePodMetrics(object raw)
    {
        static long CpuMilli(string? q)
        {
            if (string.IsNullOrEmpty(q)) return 0;
            if (q.EndsWith("m")) return long.TryParse(q[..^1], out var m) ? m : 0;
            if (q.EndsWith("u")) return long.TryParse(q[..^1], out var u) ? (long)Math.Ceiling(u / 1000.0) : 0;
            if (q.EndsWith("n")) return long.TryParse(q[..^1], out var n) ? (long)Math.Ceiling(n / 1_000_000.0) : 0;
            return double.TryParse(q, out var whole) ? (long)Math.Ceiling(whole * 1000) : 0;
        }
        static double MemMiB(string? q)
        {
            if (string.IsNullOrEmpty(q)) return 0;
            // Kubernetes 内存用量恒为二进制单位（Ki/Mi/Gi/Ti），十进制后缀视为异常按二进制折算。
            var num = q.EndsWith("i") ? q[..^2] : q;
            if (!double.TryParse(num, out var v)) return 0;
            return q.EndsWith("Ki") ? v / 1024 : q.EndsWith("Gi") ? v * 1024
                : q.EndsWith("Ti") ? v * 1024 * 1024 : v;
        }

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(raw));
        var root = doc.RootElement;
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return new PodMetricsResult(true, null, Array.Empty<PodMetricSummary>());

        var pods = new List<PodMetricSummary>();
        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var ns = item.TryGetProperty("metadata", out var m2) && m2.TryGetProperty("namespace", out var nsEl) ? nsEl.GetString() ?? "" : "";
            var containers = new List<ContainerMetric>();
            if (item.TryGetProperty("containers", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cs.EnumerateArray())
                {
                    var cname = c.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "";
                    string? cpu = null, mem = null;
                    if (c.TryGetProperty("usage", out var usage))
                    {
                        cpu = usage.TryGetProperty("cpu", out var cu) ? cu.GetString() : null;
                        mem = usage.TryGetProperty("memory", out var mu) ? mu.GetString() : null;
                    }
                    containers.Add(new ContainerMetric(cname, CpuMilli(cpu), Math.Round(MemMiB(mem), 1)));
                }
            }
            pods.Add(new PodMetricSummary(ns, name, "", containers.Sum(c => c.CpuMilli),
                Math.Round(containers.Sum(c => c.MemMiB), 1), containers));
        }
        return new PodMetricsResult(true, null, pods);
    }

    /// <summary>集群健康总览（backlog C2）：每集群节点/Pod 汇总，巡检失败降级为 Error 说明。</summary>
    public sealed record ClusterHealth(string Cluster, string Version, int NodesTotal, int NodesNotReady,
        int PodsTotal, IReadOnlyList<string> UnhealthyPods, string? Error);

    /// <summary>全部集群健康快照：判定口径与 K8sMonitor.FindUnhealthy 一致（BackOff 类原因或重启激增）。</summary>
    public IReadOnlyList<ClusterHealth> Health()
    {
        var result = new List<ClusterHealth>();
        foreach (var meta in store.List())
        {
            try
            {
                var nodes = ListNodes(meta.Name);
                var pods = ListPods(meta.Name, "");
                var unhealthy = pods
                    .Where(p => p.Reason is not null || p.Restarts >= K8sMonitor.RestartSpikeThreshold)
                    .Select(p => $"{p.Namespace}/{p.Name}")
                    .ToList();
                result.Add(new ClusterHealth(meta.Name, meta.Version, nodes.Count,
                    nodes.Count(n => n.Status != "Ready"), pods.Count, unhealthy, null));
            }
            catch (Exception ex)
            {
                result.Add(new ClusterHealth(meta.Name, meta.Version, 0, 0, 0, Array.Empty<string>(), ex.Message));
            }
        }
        return result;
    }

    public IReadOnlyList<EventSummary> ListEvents(string cluster, string ns)
    {
        using var client = Client(cluster);
        var events = string.IsNullOrEmpty(ns)
            ? client.CoreV1.ListEventForAllNamespaces()
            : client.CoreV1.ListNamespacedEvent(ns);
        return events.Items
            .OrderByDescending(e => e.LastTimestamp ?? e.Metadata.CreationTimestamp)
            .Take(200)
            .Select(e => new EventSummary(e.Metadata.NamespaceProperty ?? "", e.Type ?? "",
                e.Reason ?? "", e.InvolvedObject?.Name ?? "", e.Message ?? "",
                (e.LastTimestamp ?? e.Metadata.CreationTimestamp) is { } ts
                    ? new DateTimeOffset(ts.ToUniversalTime()).ToUnixTimeMilliseconds() : null))
            .ToList();
    }

    /// <summary>Pod 日志（读操作，端点侧进审计留痕——安全评审保留意见③）。</summary>
    public string GetPodLog(string cluster, string ns, string pod, string? container)
    {
        using var client = Client(cluster);
        using var stream = client.CoreV1.ReadNamespacedPodLog(pod, ns, container: container, tailLines: 500);
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>扩缩容：Patch deployment scale 子资源。</summary>
    public WorkloadSummary Scale(string cluster, string ns, string deployment, int replicas)
    {
        if (replicas is < 0 or > 100)
            throw new ArgumentException("replicas 超出范围（0-100）");
        using var client = Client(cluster);
        var patch = new V1Patch(
            "{\"spec\":{\"replicas\":" + replicas + "}}", V1Patch.PatchType.StrategicMergePatch);
        var d = client.AppsV1.PatchNamespacedDeploymentScale(patch, deployment, ns);
        return new WorkloadSummary("Deployment", deployment, ns,
            (int)(d.Status?.Replicas ?? 0), replicas, null, null);
    }

    /// <summary>重启（rollout restart）：patch 模板 restartedAt 注解。</summary>
    public void Restart(string cluster, string ns, string deployment)
    {
        using var client = Client(cluster);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var patch = new V1Patch(
            "{\"spec\":{\"template\":{\"metadata\":{\"annotations\":{\"taoops.io/restartedAt\":\"" + now + "\"}}}}}",
            V1Patch.PatchType.StrategicMergePatch);
        client.AppsV1.PatchNamespacedDeployment(patch, deployment, ns);
    }

    /// <summary>滚动更新进度：读 deployment status。</summary>
    public RolloutInfo RolloutStatus(string cluster, string ns, string deployment)
    {
        using var client = Client(cluster);
        var d = client.AppsV1.ReadNamespacedDeploymentStatus(deployment, ns);
        var desired = (int)(d.Spec.Replicas ?? 0);
        var updated = (int)(d.Status.UpdatedReplicas ?? 0);
        var available = (int)(d.Status.AvailableReplicas ?? 0);
        var complete = desired > 0 && updated >= desired && available >= desired;
        return new RolloutInfo(deployment, desired, updated, available, complete,
            complete ? "rollout complete" : "rollout in progress");
    }

    /// <summary>部署层：模板渲染清单直发集群（Deployment+Service，PVC 预留）。</summary>
    public DeployResult Deploy(string cluster, string templateId, string appName, string ns,
        Dictionary<string, string>? envOverride = null, int replicas = 1)
    {
        var rendered = K8sManifestRenderer.Render(templateId, appName, ns, envOverride, replicas);
        using var client = Client(cluster);
        int deployments = 0, services = 0;
        foreach (var doc in rendered.Documents)
        {
            if (doc.Contains("kind: Deployment"))
            {
                var dep = KubernetesYaml.Deserialize<V1Deployment>(doc)
                    ?? throw new InvalidOperationException("清单反序列化失败");
                Apply(() => client.AppsV1.CreateNamespacedDeployment(dep, ns),
                    () => client.AppsV1.ReplaceNamespacedDeployment(dep, dep.Metadata.Name, ns));
                deployments++;
            }
            else if (doc.Contains("kind: Service"))
            {
                var svc = KubernetesYaml.Deserialize<V1Service>(doc)
                    ?? throw new InvalidOperationException("清单反序列化失败");
                Apply(() => client.CoreV1.CreateNamespacedService(svc, ns),
                    () => client.CoreV1.ReplaceNamespacedService(svc, svc.Metadata.Name, ns));
                services++;
            }
        }
        return new DeployResult(appName, ns, deployments, services);
    }

    /// <summary>创建或替换（409 Conflict 视为已存在，转替换）。</summary>
    private static void Apply(Func<object> create, Func<object> replace)
    {
        try
        {
            create();
        }
        catch (k8s.Autorest.HttpOperationException ex)
            when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            replace();
        }
    }

    /// <summary>卸载：按 app.kubernetes.io/name 标签删除 Deployment+Service，需 confirm=true。</summary>
    public (int Deployments, int Services) Remove(string cluster, string appName, string ns, bool confirm)
    {
        if (!confirm) throw new ArgumentException("卸载需 confirm=true");
        using var client = Client(cluster);
        var label = $"app.kubernetes.io/name={appName}";
        int deployments = 0, services = 0;
        var deps = client.AppsV1.ListNamespacedDeployment(ns, labelSelector: label);
        foreach (var d in deps.Items)
        {
            client.AppsV1.DeleteNamespacedDeployment(d.Metadata.Name, ns);
            deployments++;
        }
        var svcs = client.CoreV1.ListNamespacedService(ns, labelSelector: label);
        foreach (var s in svcs.Items)
        {
            client.CoreV1.DeleteNamespacedService(s.Metadata.Name, ns);
            services++;
        }
        return (deployments, services);
    }
}
