using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaoOps.Server.Waf;

/// <summary>
/// WAF 规则存储：JSON 文件真源，落 TAOOPS_DATA_DIR/waf/rules.json。
/// 写盘一律 tmp+move 原子替换（家法三.6，防断电整本清零）。
/// </summary>
public sealed class WafRuleStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public WafRuleStore(string dataDir)
    {
        var dir = Path.Combine(dataDir, "waf");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "rules.json");
    }

    public string FilePath => _filePath;

    /// <summary>读取当前规则集；文件不存在时返回默认空集（不落盘，首次保存才建文件）。</summary>
    public WafRuleSet Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return new WafRuleSet();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<WafRuleSet>(json, JsonOpts) ?? new WafRuleSet();
        }
    }

    /// <summary>整本保存（tmp+move 原子替换）。</summary>
    public void Save(WafRuleSet ruleSet)
    {
        lock (_lock)
        {
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ruleSet, JsonOpts));
            File.Move(tmp, _filePath, overwrite: true);
        }
    }

    /// <summary>新增规则；Id 为空时自动生成。返回保存后的规则。</summary>
    public WafRule Add(WafRule rule)
    {
        var set = Load();
        if (string.IsNullOrWhiteSpace(rule.Id))
            rule.Id = Guid.NewGuid().ToString("N");
        else if (set.Rules.Any(r => r.Id == rule.Id))
            throw new InvalidOperationException($"waf rule id already exists: {rule.Id}");
        set.Rules.Add(rule);
        Save(set);
        return rule;
    }

    /// <summary>按 Id 更新规则；找不到返回 false。</summary>
    public bool Update(WafRule rule)
    {
        var set = Load();
        var idx = set.Rules.FindIndex(r => r.Id == rule.Id);
        if (idx < 0) return false;
        set.Rules[idx] = rule;
        Save(set);
        return true;
    }

    /// <summary>按 Id 删除规则；找不到返回 false。</summary>
    public bool Remove(string id)
    {
        var set = Load();
        var removed = set.Rules.RemoveAll(r => r.Id == id) > 0;
        if (removed) Save(set);
        return removed;
    }

    /// <summary>只改启停状态；找不到返回 false。</summary>
    public bool SetEnabled(string id, bool enabled)
    {
        var set = Load();
        var rule = set.Rules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return false;
        rule.Enabled = enabled;
        Save(set);
        return true;
    }

    /// <summary>保存全局设置。</summary>
    public void SaveSettings(WafSettings settings)
    {
        var set = Load();
        set.Settings = settings;
        Save(set);
    }

    /// <summary>导出规则集为 JSON 字符串（数据之道：格式不锁死，可迁出）。</summary>
    public string Export() => JsonSerializer.Serialize(Load(), JsonOpts);

    /// <summary>导入规则集整本替换（先校验再落盘，坏数据不进真源）。</summary>
    public void Import(string json)
    {
        var set = JsonSerializer.Deserialize<WafRuleSet>(json, JsonOpts)
            ?? throw new JsonException("waf import: payload is not a valid rule set");
        Save(set);
    }
}
