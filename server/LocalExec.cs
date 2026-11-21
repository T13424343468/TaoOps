using System.Diagnostics;

namespace TaoOps.Server.Services;

/// <summary>本机命令执行（M1 回环用）。白名单校验在 AgentService，本类只负责执行。</summary>
public static class LocalExec
{
    public static (int ExitCode, string Output) Execute(string command, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start: {command}");
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            proc.Kill(entireProcessTree: true);
            throw new TimeoutException($"command timed out: {command}");
        }
        return (proc.ExitCode, Truncate(output, 1_000_000));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
