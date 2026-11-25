using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaoOps.Server.Services.K8s;

/// <summary>
/// FR-A28 集群接入存储：kubeconfig 落盘 dataDir/k8s/clusters/&lt;name&gt;.kubeconfig
/// （权限 600），元数据登记 clusters.json（名称、接入时间、server、版本）。
/// 安全红线：kubeconfig 只存中控、不进审计、不进日志、绝不下发节点 Agent。
/// </summary>
public sealed class KubeClusterStore
{
    private readonly string _rootDir;
    private readonly string _metaPath;
    private readonly Func<DateTimeOffset> _clock;

    public KubeClusterStore(string dataDir, Func<DateTimeOffset>? clock = null)
    {
        _rootDir = Path.Combine(dataDir, "k8s", "clusters");
        Directory.CreateDirectory(_rootDir);
        _metaPath = Path.Combine(_rootDir, "clusters.json");
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public sealed record ClusterMeta(string Name, string Server, string Version, long AddedMs);

    private sealed class MetaFile
    {
        public List<ClusterMeta> Clusters { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>当前集群元数据清单（不含任何凭据内容）。</summary>
    public IReadOnlyList<ClusterMeta> List()
    {
        if (!File.Exists(_metaPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<MetaFile>(File.ReadAllText(_metaPath), JsonOpts)?.Clusters ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>落盘权限 600（验收口径）；受限平台尽力而为不抛错（口径同 TotpStore）。</summary>
    private static void TryChmod600(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 保存 kubeconfig（600）并登记元数据。server 地址从 kubeconfig 内容解析；
    /// version 由调用方连接测试成功后回填。重名覆盖（幂等更新）。
    /// </summary>
    public ClusterMeta Save(string name, string kubeconfig, string version)
    {
        var server = KubeconfigParser.ExtractServer(kubeconfig)
            ?? throw new ArgumentException("kubeconfig 中未找到 server 地址");
        File.WriteAllText(Path.Combine(_rootDir, $"{name}.kubeconfig"), kubeconfig,
            new UTF8Encoding(false));
        TryChmod600(Path.Combine(_rootDir, $"{name}.kubeconfig"));

        var metas = List().Where(c => !string.Equals(c.Name, name, StringComparison.Ordinal)).ToList();
        var meta = new ClusterMeta(name, server, version, _clock().ToUnixTimeMilliseconds());
        metas.Add(meta);
        File.WriteAllText(_metaPath, JsonSerializer.Serialize(new MetaFile { Clusters = metas }, JsonOpts),
            new UTF8Encoding(false));
        TryChmod600(_metaPath);
        return meta;
    }

    /// <summary>读取指定集群的 kubeconfig 原文（仅中控内部使用，不进审计/日志）。</summary>
    public string? Load(string name)
    {
        var path = Path.Combine(_rootDir, $"{name}.kubeconfig");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>删除集群：需 confirm=true；同时删除 kubeconfig 文件与元数据。</summary>
    public bool Remove(string name, bool confirm)
    {
        if (!confirm) return false;
        var path = Path.Combine(_rootDir, $"{name}.kubeconfig");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        var metas = List().Where(c => !string.Equals(c.Name, name, StringComparison.Ordinal)).ToList();
        File.WriteAllText(_metaPath, JsonSerializer.Serialize(new MetaFile { Clusters = metas }, JsonOpts),
            new UTF8Encoding(false));
        return true;
    }
}

/// <summary>
/// kubeconfig YAML 子集解析：只提取当前 context 指向的 cluster.server
/// （供元数据登记与连接测试）。不做通用 YAML 解析，凭据内容不落日志。
/// </summary>
public static class KubeconfigParser
{
    /// <summary>提取 current-context 对应 cluster 的 server 地址；解析不到返回 null。</summary>
    public static string? ExtractServer(string kubeconfig)
    {
        if (string.IsNullOrWhiteSpace(kubeconfig)) return null;
        var lines = kubeconfig.Replace("\r\n", "\n").Split('\n');
        string? currentContext = null;
        var contexts = new Dictionary<string, string>(StringComparer.Ordinal);
        var clusters = new Dictionary<string, string>(StringComparer.Ordinal);

        // 三遍扫描：current-context 行、contexts 段（name→cluster）、clusters 段（name→server）。
        string? section = null, itemName = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = raw.TakeWhile(char.IsWhiteSpace).Count();
            var trimmed = line.Trim();

            if (indent == 0 && !trimmed.StartsWith('-') && !trimmed.StartsWith('#'))
            {
                itemName = null;
                if (trimmed.StartsWith("current-context:"))
                {
                    currentContext = trimmed["current-context:".Length..].Trim().Trim('"', '\'');
                    section = null;
                }
                else if (trimmed.EndsWith(':'))
                {
                    section = trimmed[..^1];
                }
                continue;
            }

            if (section == "contexts")
            {
                if (trimmed.StartsWith("- name:"))
                    itemName = trimmed["- name:".Length..].Trim().Trim('"', '\'');
                else if (itemName is not null && trimmed.StartsWith("cluster:"))
                    contexts[itemName] = trimmed["cluster:".Length..].Trim().Trim('"', '\'');
            }
            else if (section == "clusters")
            {
                if (trimmed.StartsWith("- name:"))
                    itemName = trimmed["- name:".Length..].Trim().Trim('"', '\'');
                else if (itemName is not null && trimmed.StartsWith("server:"))
                    clusters[itemName] = trimmed["server:".Length..].Trim().Trim('"', '\'');
            }
        }

        if (currentContext is null) return null;
        return contexts.TryGetValue(currentContext, out var cluster) &&
               clusters.TryGetValue(cluster, out var server)
            ? server
            : null;
    }
}
