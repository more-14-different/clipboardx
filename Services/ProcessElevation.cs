using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;

namespace ClipboardManager;

/// <summary>
/// 检测当前进程是否已提升，以及以管理员身份重启（UAC）。
/// </summary>
public static class ProcessElevation
{
    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 以管理员身份启动与当前进程相同的可执行文件并传递相同命令行参数；成功启动则返回 true（调用方应退出当前进程）。
    /// </summary>
    public static bool TryStartElevatedCopyAndExit(string[]? startupArgs)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return false;
        // dotnet run 时 ProcessPath 为 dotnet.exe，提升会误启 SDK 宿主
        if (exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            };
            if (startupArgs is { Length: > 0 })
            {
                psi.Arguments = string.Join(" ",
                    startupArgs.Select(EscapeArgumentForProcessStart));
            }

            using var p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            // 用户取消 UAC 等
            return false;
        }
    }

    /// <summary>
    /// 从<strong>已提升</strong>进程启动同一 exe 的非提升实例（常见为 <c>cmd /c start ""</c>），成功则返回 true（调用方应退出当前进程）。
    /// </summary>
    public static bool TryStartUnelevatedCopyAndExit(string[]? startupArgs)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return false;
        if (exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var argTail = startupArgs is { Length: > 0 }
                ? " " + string.Join(" ", startupArgs.Select(EscapeArgumentForProcessStart))
                : "";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" \"{exe}\"{argTail}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>同路径、同参数再启动一实例（不请求 UAC），用于同权限下重启。</summary>
    public static bool TryStartSameExeCopy(string[]? startupArgs)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return false;
        if (exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true
            };
            if (startupArgs is { Length: > 0 })
                psi.Arguments = string.Join(" ", startupArgs.Select(EscapeArgumentForProcessStart));
            using var p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeArgumentForProcessStart(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }
}
