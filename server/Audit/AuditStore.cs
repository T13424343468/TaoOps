using System.IO;
using System.Text;
using System.Text.Json;

namespace TaoOps.Server.Audit;

/// <summary>
/// 审计账本（FR-D4）：JSONL 追加落盘（dataDir/audit/audit.jsonl）+ 内存索引。
/// 哈希链：每条含前条 Hash；重载与校验对称口径。只追加不重写，流水天然原子。
/// </summary>
public sealed class AuditStore
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<AuditEvent> _events = [];

    public AuditStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "audit");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "audit.jsonl");
        Reload();
    }

    public string FilePath => _filePath;

    /// <summary>从账本文件重载内存索引（校验用：篡改文件后重载再 Verify 可检出）。</summary>
    public void Reload()
    {
        lock (_lock)
        {
            _events.Clear();
            if (!File.Exists(_filePath)) return;
            foreach (var line in File.ReadLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var ev = JsonSerializer.Deserialize<AuditEvent>(line, JsonOpts);
                    if (ev is not null) _events.Add(ev);
                }
                catch (JsonException)
                {
                    // 畸形行跳过不崩；账本被外部破坏由 VerifyChain 定谳，不在此静默吞掉证据。
                }
            }
        }
    }

    /// <summary>留痕：接续哈希链追加一条，落盘 + 入索引。返回落账后的事件。</summary>
    public AuditEvent Append(AuditEvent ev)
    {
        lock (_lock)
        {
            ev.Seq = _events.Count > 0 ? _events[^1].Seq + 1 : 1;
            ev.PrevHash = _events.Count > 0 ? _events[^1].Hash : "";
            ev.ComputeHash();
            File.AppendAllText(_filePath, JsonSerializer.Serialize(ev, JsonOpts) + "\n");
            _events.Add(ev);
            return ev;
        }
    }

    /// <summary>查询：按用户与时间范围（毫秒）过滤，按序号倒序（最新在前）。</summary>
    public IReadOnlyList<AuditEvent> Query(string? user = null, long? fromMs = null, long? toMs = null)
    {
        lock (_lock)
        {
            return _events
                .Where(e => user is null || e.User == user)
                .Where(e => fromMs is null || e.Ts >= fromMs)
                .Where(e => toMs is null || e.Ts <= toMs)
                .OrderByDescending(e => e.Seq)
                .ToList();
        }
    }

    /// <summary>
    /// 全链校验：逐条重算哈希并核对前链指针；账本为空视为通过。
    /// 返回 (是否完整, 首个断链序号或 null)。
    /// </summary>
    public (bool Valid, long? BrokenAtSeq) VerifyChain()
    {
        lock (_lock)
        {
            var prev = "";
            foreach (var ev in _events)
            {
                var expect = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ev.Canonical()))).ToLowerInvariant();
                if (ev.PrevHash != prev || ev.Hash != expect)
                    return (false, ev.Seq);
                prev = ev.Hash;
            }
            return (true, null);
        }
    }

    /// <summary>导出 JSON（数据之道：格式不锁死，可迁出）。</summary>
    public string ExportJson() =>
        JsonSerializer.Serialize(Query(), JsonOpts);

    /// <summary>导出 CSV（Excel 友好；字段内引号转义）。</summary>
    public string ExportCsv()
    {
        var sb = new StringBuilder("seq,ts,user,role,action,target,ip,result\n");
        foreach (var e in Query().OrderBy(e => e.Seq))
        {
            sb.Append(string.Join(',',
                e.Seq, e.Ts, Csv(e.User), Csv(e.Role), Csv(e.Action), Csv(e.Target), Csv(e.Ip), Csv(e.Result)));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Csv(string field) =>
        field.Contains('"') || field.Contains(',') ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
}
