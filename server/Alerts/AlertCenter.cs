using System.Collections.Concurrent;

namespace TaoOps.Server.Alerts;

/// <summary>
/// 告警中心（FR-A19）：统一告警入口。落盘在 AlertStore（dataDir/alerts/），
/// 本类负责来源接线：磁盘水位（心跳边缘触发 + 迟滞复位）与入侵检测告警转发。
/// critical 触发推送已接 FR-A20（PushDispatcher 异步触发）。
/// </summary>
public sealed class AlertCenter
{
    /// <summary>磁盘水位 critical 阈值（PRD 映射表默认 90%；backlog F4 起可经设置页落盘调整）。</summary>
    public const int DiskCriticalPctDefault = 90;
    /// <summary>迟滞复位阈值：回落到 85% 以下才允许再次边缘触发（默认值）。</summary>
    public const int DiskResetPctDefault = 85;

    private readonly AlertStore _store;
    private readonly ConcurrentDictionary<string, bool> _diskAlerted = new(StringComparer.Ordinal);
    /// <summary>critical 触发推送（FR-A20）：接线后异步触发，可为空（测试直构）。</summary>
    private readonly Push.PushDispatcher? _push;
    /// <summary>阈值设置（backlog F4）：未接设置存储时用 PRD 默认值。</summary>
    private readonly Settings.AlertThresholdSettings _thresholds;

    public AlertCenter(AlertStore store, Push.PushDispatcher? push = null, Settings.SettingsStore? settings = null)
    {
        _store = store;
        _push = push;
        _thresholds = settings?.Load() ?? new Settings.AlertThresholdSettings();
    }

    /// <summary>当前生效阈值（设置页回显用）。</summary>
    public int DiskCriticalPct => _thresholds.DiskCriticalPct;
    public int DiskResetPct => _thresholds.DiskResetPct;

    /// <summary>重载阈值（backlog F4：设置页保存后即时生效，无需重启）。</summary>
    public void ReloadThresholds(Settings.SettingsStore settings)
    {
        var loaded = settings.Load();
        _thresholds.DiskCriticalPct = loaded.DiskCriticalPct;
        _thresholds.DiskResetPct = loaded.DiskResetPct;
    }

    /// <summary>统一告警入口：来源方直接落账；critical 触发推送（FR-A20 接线）。</summary>
    public Alert Raise(string source, string level, string nodeId, string detail, long? tsMs = null)
    {
        var alert = _store.Append(source, level, nodeId, detail, tsMs);
        _push?.OnAlert(alert);
        return alert;
    }

    /// <summary>入侵检测告警转发：级别按 severity 映射（high→critical / medium→warn / low→info）。</summary>
    public Alert IngestIntrusion(Sec.SecAlert secAlert) =>
        Raise("intrusion", AlertLevels.FromIntrusionSeverity(secAlert.Severity), secAlert.NodeId,
            $"{secAlert.Type}: {secAlert.Detail}", secAlert.Ts);

    /// <summary>
    /// 磁盘水位检查（心跳接线）：≥90% 边缘触发 critical（同节点不重复），
    /// 回落 &lt;85% 迟滞复位后允许再次触发。
    /// </summary>
    public Alert? CheckDiskWatermark(string nodeId, double diskPercent, long? tsMs = null)
    {
        var alreadyAlerted = _diskAlerted.TryGetValue(nodeId, out var flag) && flag;
        if (diskPercent >= DiskCriticalPct)
        {
            if (alreadyAlerted) return null;
            _diskAlerted[nodeId] = true;
            return Raise("disk", AlertLevels.Critical, nodeId,
                $"disk usage {diskPercent:0.#}% >= {DiskCriticalPct}%", tsMs);
        }
        if (diskPercent < DiskResetPct) _diskAlerted[nodeId] = false;
        return null;
    }
}
