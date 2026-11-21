using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaoOps.Agent.Metrics;

public readonly record struct MetricsSnapshot(double Cpu, double Mem, double Disk, double Load);

/// <summary>系统指标采集（Linux /proc，契约 HeartbeatMsg 字段）。</summary>
public static class SystemInfo
{
    public const string AgentVersion = "0.1.0";
    public static string OsName => RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        ? ReadFirstLine("/etc/os-release").Replace("\"", "").Replace("PRETTY_NAME=", "")
        : RuntimeInformation.OSDescription;
    public static string Arch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        var a => a.ToString(),
    };

    private static string ReadFirstLine(string path) =>
        File.Exists(path) ? File.ReadLines(path).FirstOrDefault() ?? "" : RuntimeInformation.OSDescription;
}

public static class MetricsCollector
{
    public static MetricsSnapshot Collect()
    {
        var (load1, memUsedPct, diskUsedPct) = (ReadLoad(), ReadMem(), ReadDisk());
        return new MetricsSnapshot(Cpu: load1 * 100, Mem: memUsedPct, Disk: diskUsedPct, Load: load1);
    }

    private static double ReadLoad()
    {
        var parts = File.ReadLines("/proc/loadavg").FirstOrDefault()?.Split(' ');
        return parts is { Length: > 0 } && double.TryParse(parts[0], out var l) ? l : 0;
    }

    private static double ReadMem()
    {
        long total = 0, free = 0, available = 0;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            var parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            var kb = ParseKb(parts[1]);
            if (parts[0] == "MemTotal") total = kb;
            else if (parts[0] == "MemFree") free = kb;
            else if (parts[0] == "MemAvailable") available = kb;
        }
        if (total == 0) return 0;
        var used = total - Math.Max(free, available);
        return 100.0 * used / total;
    }

    private static double ReadDisk()
    {
        var di = new DriveInfo("/");
        return di.IsReady ? 100.0 * (di.TotalSize - di.AvailableFreeSpace) / di.TotalSize : 0;
    }

    private static long ParseKb(string s) =>
        long.TryParse(s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var v) ? v : 0;
}
