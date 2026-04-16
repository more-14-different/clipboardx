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

    private static string EscapeArgumentForProcessStart(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }
}
