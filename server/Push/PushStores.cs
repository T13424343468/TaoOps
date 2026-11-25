using System.Net;
using System.Text.Json;

namespace TaoOps.Server.Push;

/// <summary>推送渠道（FR-A20）：用户自填 webhook，Admin 管理。</summary>
public sealed class PushChannel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public long CreatedMs { get; set; }
}

/// <summary>渠道配置落盘（dataDir/push/channels.json）。</summary>
public sealed class PushChannelStore
{
    private static readonly JsonSerializerOptions JsonOpts = new();
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<PushChannel> _channels = [];

    public PushChannelStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "push");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "channels.json");
        if (File.Exists(_filePath))
        {
            try
            {
                _channels = JsonSerializer.Deserialize<List<PushChannel>>(File.ReadAllText(_filePath), JsonOpts) ?? [];
            }
            catch (JsonException) { _channels = []; }
        }
    }

    private void Save()
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_channels, JsonOpts));
    }

    public IReadOnlyList<PushChannel> List()
    {
        lock (_lock) return _channels.ToList();
    }

    /// <summary>新增或更新（id 非空则更新）；URL 由调用方先行 SsrfGuard 校验。</summary>
    public PushChannel Upsert(PushChannel channel)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(channel.Id))
            {
                channel.Id = Guid.NewGuid().ToString("N");
                channel.CreatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _channels.Add(channel);
            }
            else
            {
                var idx = _channels.FindIndex(c => c.Id == channel.Id);
                if (idx < 0) throw new KeyNotFoundException($"channel {channel.Id} not found");
                channel.CreatedMs = _channels[idx].CreatedMs;
                _channels[idx] = channel;
            }
            Save();
            return channel;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _channels.RemoveAll(c => c.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }
}

/// <summary>SSRF 白名单（dataDir/push/ssrf-whitelist.json）：条目为主机名精确匹配或 CIDR 网段；默认空。</summary>
public sealed class SsrfWhitelistStore
{
    private static readonly JsonSerializerOptions JsonOpts = new();
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<string> _entries = [];

    public SsrfWhitelistStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "push");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "ssrf-whitelist.json");
        if (File.Exists(_filePath))
        {
            try
            {
                _entries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath), JsonOpts) ?? [];
            }
            catch (JsonException) { _entries = []; }
        }
    }

    private void Save() => File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries, JsonOpts));

    public IReadOnlyList<string> List()
    {
        lock (_lock) return _entries.ToList();
    }

    /// <summary>整体替换；返回（旧表，新表）供审计逐条对账。</summary>
    public (IReadOnlyList<string> Old, IReadOnlyList<string> New) Replace(IReadOnlyList<string> entries)
    {
        lock (_lock)
        {
            var old = _entries.ToList();
            _entries = entries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Save();
            return (old, _entries.ToList());
        }
    }
}

/// <summary>推送日志一条：每次尝试独立留痕。</summary>
public sealed class PushLogEntry
{
    public long Ts { get; set; }
    public string ChannelId { get; set; } = "";
    public string Url { get; set; } = "";
    public int? StatusCode { get; set; }
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public int Attempt { get; set; }
}

/// <summary>推送日志落盘（dataDir/push/log.jsonl），保留策略同告警（默认 90 天）。</summary>
public sealed class PushLogStore
{
    private static readonly JsonSerializerOptions JsonOpts = new();
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<PushLogEntry> _entries = [];
    private readonly long _retentionMs;

    public PushLogStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "push");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "log.jsonl");
        var days = long.TryParse(Environment.GetEnvironmentVariable("TAOOPS_ALERTS_RETENTION_DAYS"), out var d) && d > 0
            ? d : 90;
        _retentionMs = days * 24 * 60 * 60 * 1000L;
        if (File.Exists(_filePath))
        {
            foreach (var line in File.ReadLines(_filePath))
            {
                try
                {
                    var e = JsonSerializer.Deserialize<PushLogEntry>(line, JsonOpts);
                    if (e is not null) _entries.Add(e);
                }
                catch (JsonException) { }
            }
        }
        Prune(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Append(PushLogEntry entry)
    {
        lock (_lock)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var expired = _entries.RemoveAll(e => e.Ts < nowMs - _retentionMs);
            if (expired > 0) Rewrite();
            File.AppendAllText(_filePath, JsonSerializer.Serialize(entry, JsonOpts) + "\n");
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<PushLogEntry> Query(string? channelId = null, long? fromMs = null, long? toMs = null)
    {
        lock (_lock)
        {
            return _entries
                .Where(e => channelId is null || e.ChannelId == channelId)
                .Where(e => fromMs is null || e.Ts >= fromMs)
                .Where(e => toMs is null || e.Ts <= toMs)
                .OrderByDescending(e => e.Ts)
                .ToList();
        }
    }

    private void Prune(long nowMs)
    {
        lock (_lock)
        {
            var before = _entries.Count;
            _entries.RemoveAll(e => e.Ts < nowMs - _retentionMs);
            if (_entries.Count != before) Rewrite();
        }
    }

    private void Rewrite()
    {
        using var writer = new StreamWriter(_filePath, append: false);
        foreach (var e in _entries)
            writer.WriteLine(JsonSerializer.Serialize(e, JsonOpts));
    }
}
