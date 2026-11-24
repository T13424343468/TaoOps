using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TaoOps.Server.Services.K8s;

/// <summary>
/// FR-A28 部署层：FR-A24 模板渲染为 K8s 清单（Deployment+Service，PVC 预留），
/// 提交到指定 namespace。镜像引用走 FR-A11 同口径正则；env 覆盖模板默认值，
/// 密码类字段渲染前审计脱敏由调用方负责（本类不落审计）。
/// </summary>
public static partial class K8sManifestRenderer
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]*(:[0-9]+)?(/[a-zA-Z0-9._/-]+)*(:[a-zA-Z0-9._-]+)?(@sha256:[a-f0-9]{64})?$")]
    private static partial Regex ImageRef();

    /// <summary>渲染异常：模板缺失、镜像不合法、应用名不合法等。</summary>
    public sealed class RenderException(string message) : Exception(message);

    /// <summary>渲染结果：多服务模板产出多组 Deployment+Service 清单（YAML 文本）。</summary>
    public sealed record RenderedApp(string AppName, string Namespace, IReadOnlyList<string> Documents);

    /// <summary>
    /// 渲染模板为 K8s 清单。envOverride 覆盖模板默认 env（同 key 覆盖，不新增注入——
    /// 与 FR-A24 口径一致：仅覆盖模板已声明的变量）。
    /// </summary>
    public static RenderedApp Render(string templateId, string appName, string ns,
        Dictionary<string, string>? envOverride = null, int replicas = 1)
    {
        var template = K8sTemplateCatalog.Get(templateId)
            ?? throw new RenderException($"模板不存在: {templateId}");
        if (!AppNameRegex().IsMatch(appName))
            throw new RenderException("应用名不合法（字母数字开头，2-63 位字母数字-）");
        if (replicas < 0 || replicas > 100)
            throw new RenderException("replicas 超出范围（0-100）");

        var documents = new List<string>();
        foreach (var svc in template.Services)
        {
            if (!ImageRef().IsMatch(svc.Image))
                throw new RenderException($"镜像引用不合法: {svc.Image}");

            var env = new Dictionary<string, string>(svc.Env, StringComparer.Ordinal);
            if (envOverride is not null)
            {
                foreach (var kv in envOverride)
                    if (env.ContainsKey(kv.Key)) env[kv.Key] = kv.Value;
            }
            // K8s DNS 语义：模板 env 中引用兄弟服务名（Compose 语义）改写为
            // <app>-<service>（与渲染出的 Service 名一致，如 db → my-blog-db）。
            var siblings = template.Services.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var key in env.Keys.ToList())
                if (siblings.Contains(env[key]))
                    env[key] = $"{appName}-{env[key]}";

            documents.Add(RenderDeployment(template, svc, appName, ns, env, replicas));
            if (svc.Ports.Length > 0)
                documents.Add(RenderService(svc, appName, ns));
        }
        return new RenderedApp(appName, ns, documents);
    }

    private static string RenderDeployment(K8sTemplateCatalog.K8sTemplate template,
        K8sTemplateCatalog.K8sService svc, string appName, string ns,
        Dictionary<string, string> env, int replicas)
    {
        var sb = new StringBuilder();
        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: Deployment");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {Yaml(appName + "-" + svc.Name)}");
        sb.AppendLine($"  namespace: {Yaml(ns)}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    app.kubernetes.io/name: {Yaml(appName)}");
        sb.AppendLine($"    app.kubernetes.io/component: {Yaml(svc.Name)}");
        sb.AppendLine($"    app.kubernetes.io/managed-by: taoops");
        sb.AppendLine("spec:");
        sb.AppendLine($"  replicas: {replicas}");
        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");
        sb.AppendLine($"      app.kubernetes.io/name: {Yaml(appName)}");
        sb.AppendLine($"      app.kubernetes.io/component: {Yaml(svc.Name)}");
        sb.AppendLine("  template:");
        sb.AppendLine("    metadata:");
        sb.AppendLine("      labels:");
        sb.AppendLine($"        app.kubernetes.io/name: {Yaml(appName)}");
        sb.AppendLine($"        app.kubernetes.io/component: {Yaml(svc.Name)}");
        sb.AppendLine("    spec:");
        sb.AppendLine("      containers:");
        sb.AppendLine($"      - name: {Yaml(svc.Name)}");
        sb.AppendLine($"        image: {Yaml(svc.Image)}");
        if (svc.Ports.Length > 0)
        {
            sb.AppendLine("        ports:");
            foreach (var mapping in svc.Ports)
            {
                var containerPort = mapping.Split(':')[^1];
                sb.AppendLine($"        - containerPort: {int.Parse(containerPort)}");
            }
        }
        if (env.Count > 0)
        {
            sb.AppendLine("        env:");
            foreach (var kv in env)
            {
                sb.AppendLine($"        - name: {Yaml(kv.Key)}");
                sb.AppendLine($"          value: {Yaml(kv.Value)}");
            }
        }
        if (svc.Volumes.Length > 0)
        {
            // PVC 预留：数据卷渲染为 PVC（ReadWriteOnce），挂载进容器。
            sb.AppendLine("        volumeMounts:");
            foreach (var vol in svc.Volumes)
            {
                var parts = vol.Split(':');
                var mountPath = parts.Length > 1 ? parts[1] : vol;
                var claimName = $"{appName}-{svc.Name}-{SanitizeVolume(parts[0])}";
                sb.AppendLine($"        - name: {Yaml(claimName)}");
                sb.AppendLine($"          mountPath: {Yaml(mountPath)}");
            }
            sb.AppendLine("      volumes:");
            foreach (var vol in svc.Volumes)
            {
                var parts = vol.Split(':');
                var claimName = $"{appName}-{svc.Name}-{SanitizeVolume(parts[0])}";
                sb.AppendLine($"      - name: {Yaml(claimName)}");
                sb.AppendLine("        persistentVolumeClaim:");
                sb.AppendLine($"          claimName: {Yaml(claimName)}");
            }
        }
        if (!string.IsNullOrEmpty(svc.HealthCmd))
        {
            sb.AppendLine($"        readinessProbe:");
            sb.AppendLine($"          exec:");
            sb.AppendLine($"            command: [\"sh\", \"-c\", \"{EscapeYamlSingle(svc.HealthCmd)}\"]");
            sb.AppendLine($"          initialDelaySeconds: 5");
            sb.AppendLine($"          periodSeconds: {Math.Max(5, svc.HealthIntervalSec)}");
            sb.AppendLine($"        livenessProbe:");
            sb.AppendLine($"          exec:");
            sb.AppendLine($"            command: [\"sh\", \"-c\", \"{EscapeYamlSingle(svc.HealthCmd)}\"]");
            sb.AppendLine($"          initialDelaySeconds: {Math.Max(10, svc.HealthIntervalSec)}");
            sb.AppendLine($"          periodSeconds: {Math.Max(10, svc.HealthIntervalSec)}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderService(K8sTemplateCatalog.K8sService svc, string appName, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Service");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {Yaml(appName + "-" + svc.Name)}");
        sb.AppendLine($"  namespace: {Yaml(ns)}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    app.kubernetes.io/name: {Yaml(appName)}");
        sb.AppendLine($"    app.kubernetes.io/component: {Yaml(svc.Name)}");
        sb.AppendLine("spec:");
        sb.AppendLine("  selector:");
        sb.AppendLine($"    app.kubernetes.io/name: {Yaml(appName)}");
        sb.AppendLine($"    app.kubernetes.io/component: {Yaml(svc.Name)}");
        sb.AppendLine("  ports:");
        foreach (var mapping in svc.Ports)
        {
            var parts = mapping.Split(':');
            sb.AppendLine($"  - port: {int.Parse(parts[0])}");
            sb.AppendLine($"    targetPort: {int.Parse(parts[^1])}");
        }
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex AppNameRegex();

    private static string SanitizeVolume(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9-]", "-").Trim('-');

    /// <summary>YAML 标量安全引用：双引号包裹并转义反斜杠与双引号。</summary>
    private static string Yaml(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    private static string EscapeYamlSingle(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
