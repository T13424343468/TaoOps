using System.Net;
using System.Text;
using System.Text.Json;

namespace TaoOps.Server.Push;

/// <summary>单次推送结果（逐渠道）。</summary>
public sealed class PushResult
{
    public string ChannelId { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Ok { get; set; }
    public int? StatusCode { get; set; }
    public string Error { get; set; } = "";
    public int Attempts { get; set; }
}

/// <summary>
/// 推送分发器（FR-A20）：critical 告警触发时向全部启用渠道 POST JSON 模板；
/// 失败重试 3 次，退避 1s/2s/4s；每次尝试独立留痕。HTTP 客户端禁自动重定向。
/// </summary>
public sealed class PushDispatcher
{
    private static readonly JsonSerializerOptions JsonOpts = new();
    private static readonly int[] BackoffMs = [1000, 2000, 4000];

    private readonly PushChannelStore _channels;
    private readonly SsrfWhitelistStore _whitelist;
    private readonly PushLogStore _log;
    private readonly Func<HttpClient> _clientFactory;
    private readonly Func<Task>? _delay;

    public PushDispatcher(PushChannelStore channels, SsrfWhitelistStore whitelist, PushLogStore log,
        Func<HttpClient>? clientFactory = null, Func<Task>? delay = null)
    {
        _channels = channels;
        _whitelist = whitelist;
        _log = log;
        _clientFactory = clientFactory ?? NewClient;
        _delay = delay;
    }

    private static HttpClient NewClient()
    {
        // 禁自动重定向（PRD：禁用自动重定向，或每次跳转复检内网判定——取禁用）。
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>告警接线入口：critical 级别异步触发（fire-and-forget，不阻塞告警入库）。</summary>
    public void OnAlert(Alerts.Alert alert)
    {
        if (alert.Level != Alerts.AlertLevels.Critical) return;
        _ = SendAsync(alert);
    }

    /// <summary>向全部启用渠道推送；返回逐渠道结果。</summary>
    public async Task<IReadOnlyList<PushResult>> SendAsync(Alerts.Alert alert)
    {
        var payload = JsonSerializer.Serialize(new
        {
            msgtype = "text",
            text = new
            {
                content = $"[TaoOps {alert.Level}] {alert.Source}/{alert.NodeId}: {alert.Detail}",
            },
            alert_id = alert.Id,
            source = alert.Source,
            level = alert.Level,
            node_id = alert.NodeId,
            detail = alert.Detail,
            ts = alert.Ts,
        }, JsonOpts);

        var results = new List<PushResult>();
        foreach (var channel in _channels.List().Where(c => c.Enabled))
        {
            results.Add(await SendOneAsync(channel, payload));
        }
        return results;
    }

    private async Task<PushResult> SendOneAsync(PushChannel channel, string payload)
    {
        var result = new PushResult { ChannelId = channel.Id, Url = channel.Url };
        for (var attempt = 1; attempt <= BackoffMs.Length + 1; attempt++)
        {
            var entry = new PushLogEntry
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ChannelId = channel.Id,
                Url = channel.Url,
                Attempt = attempt,
            };
            try
            {
                using var client = _clientFactory();
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await client.PostAsync(channel.Url, content);
                entry.StatusCode = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    entry.Ok = true;
                    result.Ok = true;
                }
                else
                {
                    entry.Error = $"HTTP {(int)resp.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
            }
            _log.Append(entry);
            if (result.Ok)
            {
                result.Attempts = attempt;
                return result;
            }
            result.Error = entry.Error;
            if (attempt <= BackoffMs.Length && _delay is not null) await _delay();
            else if (attempt <= BackoffMs.Length) await Task.Delay(BackoffMs[attempt - 1]);
        }
        result.Attempts = BackoffMs.Length + 1;
        return result;
    }
}
