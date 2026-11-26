using System.IO;
using System.Text.Json;

namespace TaoOps.Server.Settings;

/// <summary>
/// 系统设置存储（backlog F4）：JSON 真源（dataDir/settings.json），tmp+move 原子写盘
/// （口径同 UserStore）。告警阈值等此前只能改环境变量，现可视化落盘。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _lock = new();

    public SettingsStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "settings.json");
    }

    public string FilePath => _filePath;

    public AlertThresholdSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return new AlertThresholdSettings();
            try
            {
                return JsonSerializer.Deserialize<AlertThresholdSettings>(File.ReadAllText(_filePath), JsonOpts)
                    ?? new AlertThresholdSettings();
            }
            catch (JsonException)
            {
                // 文件损坏（半写/手工误改）时回退默认值，不让面板起不来；下次 Save 覆盖修复。
                return new AlertThresholdSettings();
            }
        }
    }

    public AlertThresholdSettings Save(AlertThresholdSettings settings)
    {
        lock (_lock)
        {
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
            File.Move(tmp, _filePath, overwrite: true);
        }
        return settings;
    }
}

/// <summary>告警阈值设置：磁盘水位 critical 与迟滞复位（默认沿用 PRD 90/85）。</summary>
public sealed class AlertThresholdSettings
{
    public int DiskCriticalPct { get; set; } = 90;
    public int DiskResetPct { get; set; } = 85;
    /// <summary>FR-A29：CPU 峰值建议阈值（%），与磁盘阈值同页配置。</summary>
    public int CpuPercent { get; set; } = 85;
    /// <summary>FR-A29：内存峰值建议阈值（%）。</summary>
    public int MemPercent { get; set; } = 85;
}
