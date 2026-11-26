using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaoOps.Server.Audit;
using TaoOps.Server.Push;

namespace TaoOps.Server.Services;

/// <summary>
/// FR-A29 外接模式（PRD v1.4 §一）：用户自备 OpenAI 兼容端点做深度分析。
/// 安全口径（安全位评审）：端点与 Key 存中控落盘 600；Key 不回显、不进日志/审计/异常堆栈；
/// 出站仅 HTTPS + SsrfGuard（私网段默认拒绝，白名单 Admin 配置）；响应体上限 1MB 流式读取；
/// 响应不落盘、审计只记调用事实；限频每节点每分钟 ≤1 次。
/// </summary>
public class AiAdviceExternal
{
    public const int MaxResponseBytes = 1024 * 1024;
    public const int TimeoutSeconds = 30;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;
    private readonly AuditStore _audit;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, long> _lastCallPerNode = new(StringComparer.Ordinal);
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    public AiAdviceExternal(string dataDir, AuditStore audit,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
        Func<DateTimeOffset>? now = null)
    {
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "aiadvice.json");
        _audit = audit;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _send = send ?? ((req, ct) => SsrfHttpClient.Instance.SendAsync(req, ct));
        TryChmod(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public sealed record ExternalConfig(string Endpoint, string ApiKey, IReadOnlyList<string> SsrfWhitelist);

    private sealed class PersistedConfig
    {
        [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
        [JsonPropertyName("api_key")] public string ApiKey { get; set; } = "";
        [JsonPropertyName("ssrf_whitelist")] public List<string> SsrfWhitelist { get; set; } = [];
    }

    private static void TryChmod(string path, UnixFileMode mode)
    {
        try
        {
            if (File.Exists(path) && OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, mode);
        }
        catch (NotSupportedException) { }
    }

    // ---- 配置读写（Admin）----

    public void SaveConfig(ExternalConfig config)
    {
        var err = SsrfGuard.ValidateUrl(config.Endpoint);
        if (err is not null) throw new ArgumentException($"invalid endpoint: {err}");
        var persisted = new PersistedConfig
        {
            Endpoint = config.Endpoint,
            ApiKey = config.ApiKey,
            SsrfWhitelist = [.. config.SsrfWhitelist],
        };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(persisted, JsonOpts));
        TryChmod(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>读取配置；Key 不出本类（返回 has_key 布尔而非明文）。</summary>
    public (string Endpoint, bool HasKey, IReadOnlyList<string> Whitelist) LoadConfigSafe()
    {
        if (!File.Exists(_filePath)) return ("", false, []);
        try
        {
            var cfg = JsonSerializer.Deserialize<PersistedConfig>(File.ReadAllText(_filePath), JsonOpts);
            return (cfg?.Endpoint ?? "", !string.IsNullOrEmpty(cfg?.ApiKey), cfg?.SsrfWhitelist ?? []);
        }
        catch (JsonException)
        {
            return ("", false, []); // 损坏回退默认（同 SettingsStore 口径）
        }
    }

    private PersistedConfig? LoadFull()
    {
        if (!File.Exists(_filePath)) return null;
        try { return JsonSerializer.Deserialize<PersistedConfig>(File.ReadAllText(_filePath), JsonOpts); }
        catch (JsonException) { return null; }
    }

    // ---- 深度分析（Operator+，限频每节点每分钟 ≤1 次）----

    /// <summary>
    /// 深度分析：把脱敏摘要（仅指标数值与规则标识，不含主机名/IP/节点名）发给用户配置端点，
    /// 返回纯文本建议。失败抛 AiExternalException（错误只含分类，不含 Key 任何片段）。
    /// </summary>
    public async Task<string> AnalyzeAsync(
        string nodeId, string summary, string user, string role, string ip, CancellationToken ct = default)
    {
        var cfg = LoadFull();
        if (cfg is null || string.IsNullOrEmpty(cfg.Endpoint) || string.IsNullOrEmpty(cfg.ApiKey))
            throw new AiExternalException(400, "external endpoint not configured");

        // 限频：每节点每分钟 ≤1 次（内存态不持久化）。
        var nowMs = _now().ToUnixTimeMilliseconds();
        var last = _lastCallPerNode.GetOrAdd(nodeId, 0);
        if (nowMs - last < 60_000)
            throw new AiExternalException(429, "rate limit exceeded (1/min per node)");
        _lastCallPerNode[nodeId] = nowMs;

        // SSRF 校验：私网段默认拒绝，白名单放行（与推送渠道同口径）。
        var guardErr = await SsrfGuard.ValidateTargetAsync(cfg.Endpoint, cfg.SsrfWhitelist);
        if (guardErr is not null)
            throw new AiExternalException(400, $"endpoint blocked: {guardErr}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result;
        try
        {
            result = await SendChatAsync(cfg.Endpoint, cfg.ApiKey, summary, ct);
        }
        catch (AiExternalException) { throw; }
        catch (Exception)
        {
            AuditCall(nodeId, user, role, ip, "error", sw.ElapsedMilliseconds);
            throw new AiExternalException(502, "endpoint unreachable or invalid response");
        }
        AuditCall(nodeId, user, role, ip, "ok", sw.ElapsedMilliseconds);
        return result;
    }

    private async Task<string> SendChatAsync(string endpoint, string apiKey, string summary, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = "default",
            messages = new[]
            {
                new { role = "system", content = "你是运维助手。根据提供的指标摘要给出简明运维建议，用纯文本，不要使用 Markdown 或 HTML。" },
                new { role = "user", content = summary },
            },
        }, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        using var resp = await _send(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new AiExternalException(502, "endpoint returned non-success status");

        // 1MB 上限流式读取，超限中断（防慢速滴灌 DoS）。
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buf.Write(chunk, 0, read);
            if (buf.Length > MaxResponseBytes)
                throw new AiExternalException(400, "response exceeds 1MB limit");
        }
        var text = System.Text.Encoding.UTF8.GetString(buf.ToArray());
        return text.Length <= MaxResponseBytes ? text : text[..MaxResponseBytes];
    }

    private void AuditCall(string nodeId, string user, string role, string ip, string result, long elapsedMs)
    {
        // 审计只记调用事实与耗时；不含 Key、不含请求与响应正文（安全位评审口径）。
        _audit.Append(new AuditEvent
        {
            Ts = _now().ToUnixTimeMilliseconds(),
            User = user, Role = role, Action = "aiadvice.external", Target = nodeId, Ip = ip,
            Result = $"{result} elapsed_ms={elapsedMs}",
        });
    }

    public sealed class AiExternalException(int statusCode, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }
}

/// <summary>外呼专用 HttpClient：禁重定向（SsrfGuard 口径），由 Program.cs 装配。</summary>
public static class SsrfHttpClient
{
    public static readonly HttpClient Instance = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(AiAdviceExternal.TimeoutSeconds + 5),
    };
}
