using System.Collections.Concurrent;

namespace TaoOps.Server.Alerts;

/// <summary>
/// 告警中心（FR-A19）：统一告警入口。落盘在 AlertStore（dataDir/alerts/），
/// 本类负责来源接线：磁盘水位（心跳边缘触发 + 迟滞复位）与入侵检测告警转发。
/// critical 触发推送（FR-A20）随其落地后补验（验收拆两段，PRD v1.2 §FR-A19）。
/// </summary>
public sealed class AlertCenter
{
    /// <summary>磁盘水位 critical 阈值（PRD 映射表：≥90%=critical）。</summary>
    public const int DiskCriticalPct = 90;
    /// <summary>迟滞复位阈值：回落到 85% 以下才允许再次边缘触发。</summary>
    public const int DiskResetPct = 85;

    private readonly AlertStore _store;
    private readonly ConcurrentDictionary<string, bool> _diskAlerted = new(StringComparer.Ordinal);

    public AlertCenter(AlertStore store) => _store = store;

    /// <summary>统一告警入口：来源方直接落账。</summary>
    public Alert Raise(string source, string level, string nodeId, string detail, long? tsMs = null) =>
        _store.Append(source, level, nodeId, detail, tsMs);

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
