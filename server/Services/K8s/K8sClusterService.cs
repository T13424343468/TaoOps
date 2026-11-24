using k8s;
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
            "{\"spec\":{\"template\":{\"metadata\":{\"annotations\":{\"taoops.io/restartedAt\":\"" + now + "\"}}}}}}",
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
