namespace TaoOps.Server;

/// <summary>
/// 平台路径与运行环境（Windows 版 FR-A31）：
/// 数据目录三段式解析——环境变量显式指定优先；Windows 默认
/// %ProgramData%\TaoOps\data；Linux 沿用 /var/lib/taoops/server（M1 起的既有约定）。
/// 前端界面随中控自带：默认取程序目录下 web/，可用 TAOOPS_WEB_DIST 覆盖（开发/测试）。
/// </summary>
public static class TaoOpsPaths
{
    /// <summary>数据目录解析（纯函数便于双向测试）。</summary>
    public static string ResolveDataDir(string? envValue, bool isWindows, string programData)
    {
        if (!string.IsNullOrEmpty(envValue)) return envValue;
        return isWindows
            ? Path.Combine(programData, "TaoOps", "data")
            : "/var/lib/taoops/server";
    }

    /// <summary>当前生效的数据目录。</summary>
    public static string DataDir =>
        ResolveDataDir(Environment.GetEnvironmentVariable("TAOOPS_DATA_DIR"),
            OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    /// <summary>
    /// 前端静态资源目录：TAOOPS_WEB_DIST 显式指定（存在才生效）；
    /// 否则取程序目录下 web/（安装器布局：界面文件随程序分发）。
    /// 未命中返回 null，保持既有行为（无静态托管，API-only）。
    /// </summary>
    public static string? ResolveWebDist(string? envValue, string baseDirectory)
    {
        if (!string.IsNullOrEmpty(envValue))
            return Directory.Exists(envValue) ? envValue : null;
        var bundled = Path.Combine(baseDirectory, "web");
        return Directory.Exists(bundled) ? bundled : null;
    }
}
