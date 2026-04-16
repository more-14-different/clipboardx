using System.IO;
using Microsoft.Win32;

namespace ClipboardManager;

/// <summary>
/// 当前用户登录时自启动（HKCU Run）。
/// </summary>
public static class StartupRegistration
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "ClipboardManager";
    private const string ValueName = "ClipboardX";

    /// <summary>
    /// 解析要写入 Run 键的可执行路径；<c>dotnet run</c> 等场景返回 null，避免误注册 dotnet.exe。
    /// </summary>
    private static string? ResolveExecutablePathForStartup()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return null;

        if (processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        if (File.Exists(processPath) &&
            processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return processPath;

        return null;
    }

    /// <param name="runAtStartup">是否写入 HKCU Run。</param>
    /// <param name="runAsAdministrator">为 true 时，自启项通过 PowerShell <c>Start-Process -Verb RunAs</c>，每次登录会弹出 UAC。</param>
    public static void Apply(bool runAtStartup, bool runAsAdministrator)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true);
            if (key == null) return;

            if (!runAtStartup)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                return;
            }

            var exePath = ResolveExecutablePathForStartup();
            if (string.IsNullOrEmpty(exePath)) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

            if (runAsAdministrator)
            {
                var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(ps))
                    ps = "powershell.exe";
                var safePath = exePath.Replace("'", "''");
                var runValue =
                    $"\"{ps}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Start-Process -LiteralPath '{safePath}' -Verb RunAs\"";
                key.SetValue(ValueName, runValue);
            }
            else
            {
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
        }
        catch
        {
            // 权限或策略失败时静默跳过
        }
    }
}
