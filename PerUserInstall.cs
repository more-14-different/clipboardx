using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace ClipboardManager;

/// <summary>
/// 将程序安装到当前用户目录（%LocalAppData%\Programs\ClipboardManager），并注册“应用和功能”卸载项。
/// </summary>
public static class PerUserInstall
{
    public const string UninstallRegistryKeyName = "ClipboardManager";
    private const string PublisherName = "ClipboardManager";

    private static readonly string InstallRootRelative =
        Path.Combine("Programs", "ClipboardManager");

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            InstallRootRelative);

    public static string InstalledExecutablePath =>
        Path.Combine(InstallDirectory, "ClipboardManager.exe");

    public static string DisplayName => "剪切板管理器";

    private static bool IsDevBypass =>
        string.Equals(Environment.GetEnvironmentVariable("CLIPBOARD_MANAGER_DEV"),
            "1", StringComparison.Ordinal);

    /// <summary>
    /// 是否允许执行“复制到用户 Program 目录”（正式分发用的 exe）；
    /// <c>dotnet.exe</c> 直接托管 dll 时 ProcessPath 为 dotnet，会跳过。
    /// </summary>
    /// <remarks>
    /// <c>dotnet run</c> 实际会启动 <c>bin\...\ClipboardManager.exe</c> apphost，ProcessPath 不是 dotnet；
    /// 若仅复制 exe 而安装目录无同名 dll，框架依赖部署会启动失败，故 Debug 构建或需整套文件复制时另有处理。
    /// </remarks>
    public static bool ShouldUsePerUserInstallPipeline()
    {
        if (IsDevBypass) return false;
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return false;
        if (path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            return path;
        }
    }

    public static bool IsRunningFromInstallLocation()
    {
        if (!ShouldUsePerUserInstallPipeline()) return false;
        var current = NormalizePath(Environment.ProcessPath!);
        var installed = NormalizePath(InstalledExecutablePath);
        return string.Equals(current, installed, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasUninstallArgument(string[] args) =>
        args.Any(static a =>
            string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从「应用和功能」或带 <c>--uninstall</c> 的进程启动时调用；返回 true 时应退出当前进程（含用户取消）。
    /// </summary>
    public static bool TryProcessUninstallArgs(string[] args)
    {
        if (!HasUninstallArgument(args)) return false;
        RunUninstallWizard(standaloneUninstallProcess: true);
        return true;
    }

    /// <summary>
    /// 从托盘菜单卸载：用户点「取消」时不退出主程序。
    /// </summary>
    public static void PromptUninstallFromTray()
    {
        if (!RunUninstallWizard(standaloneUninstallProcess: false)) return;
        System.Windows.Application.Current.Shutdown();
    }

    /// <returns>是否已执行卸载流程至结束（用户选「取消」时为 false）</returns>
    private static bool RunUninstallWizard(bool standaloneUninstallProcess)
    {
        var verify = System.Windows.MessageBox.Show(
            "将卸载剪切板管理器、移除开机启动与「应用和功能」条目，并删除安装目录中的程序文件。\n\n是否同时删除配置与历史记录？（%AppData%\\ClipboardManager）\n\n「是」删除程序与配置；「否」只删程序；「取消」中止。",
            "卸载剪切板管理器",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);

        if (verify == System.Windows.MessageBoxResult.Cancel)
        {
            if (standaloneUninstallProcess)
                System.Windows.MessageBox.Show("已取消卸载。", DisplayName, System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            return false;
        }

        var removeAppData = verify == System.Windows.MessageBoxResult.Yes;

        try
        {
            StartupRegistration.Apply(false);
        }
        catch { /* ignore */ }

        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
            parent?.DeleteSubKeyTree(UninstallRegistryKeyName, throwOnMissingSubKey: false);
        }
        catch { /* ignore */ }

        if (removeAppData)
        {
            try
            {
                var appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClipboardManager");
                if (Directory.Exists(appDataDir))
                    Directory.Delete(appDataDir, recursive: true);
            }
            catch { /* ignore */ }
        }

        var dir = InstallDirectory;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 2 /nobreak >nul && rd /s /q \"{dir}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
        }
        catch
        {
            System.Windows.MessageBox.Show(
                "无法启动清理任务。请手动删除文件夹：\n" + dir,
                "卸载", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }

        System.Windows.MessageBox.Show(
            standaloneUninstallProcess
                ? "卸载已完成或即将完成。程序将退出。"
                : "卸载已完成或即将完成。程序即将退出。",
            "卸载", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return true;
    }

    /// <summary>
    /// 若当前正在安装目录下运行，确保卸载注册表项存在（例如用户曾手动清理注册表）。
    /// </summary>
    public static void EnsureUninstallRegistrationIfNeeded()
    {
        if (!IsRunningFromInstallLocation()) return;
        try
        {
            using var check = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallRegistryKeyName}");
            if (check != null) return;
        }
        catch { return; }

        WriteUninstallRegistry(InstalledExecutablePath);
    }

    /// <summary>
    /// 非安装目录下的发布版 exe：复制到用户 Programs 并启动副本；返回 true 时应退出当前进程。
    /// </summary>
    public static bool TryInstallToUserProgramsAndRelaunch(IReadOnlyList<string> args)
    {
#if DEBUG
        // dotnet run 默认 Debug：apphost 在 bin\...，不应触发安装（否则只拷 exe、安装目录缺 dll 会报错并误退出）
        _ = args.Count;
        return false;
#else
        if (!ShouldUsePerUserInstallPipeline()) return false;
        if (IsRunningFromInstallLocation()) return false;

        try
        {
            Directory.CreateDirectory(InstallDirectory);
            var source = Environment.ProcessPath!;
            var sourceDir = Path.GetDirectoryName(source)!;

            // 框架依赖：apphost 旁有 ClipboardManager.dll，需整套复制；单文件发布仅有大 exe 则无此文件
            if (File.Exists(Path.Combine(sourceDir, "ClipboardManager.dll")))
                CopyFrameworkDeploymentFiles(sourceDir, InstallDirectory);
            else
                File.Copy(source, InstalledExecutablePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "未能复制程序到安装目录（可能被杀软拦截或无权写入）：\n" + ex.Message +
                "\n\n将尝试从当前位置继续运行。",
                DisplayName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        WriteUninstallRegistry(InstalledExecutablePath);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = InstalledExecutablePath,
                UseShellExecute = false,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "已安装到：\n" + InstalledExecutablePath +
                "\n\n但无法启动，请手动运行该路径下程序：\n" + ex.Message,
                DisplayName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }

        return true;
#endif
    }

    /// <summary>将 bin 输出中与运行相关的文件复制到安装目录（不含 .pdb）。</summary>
    private static void CopyFrameworkDeploymentFiles(string sourceDir, string destDir)
    {
        static bool ShouldCopy(string fileName)
        {
            var ext = Path.GetExtension(fileName);
            if (ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase)) return false;
            return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var path in Directory.EnumerateFiles(sourceDir))
        {
            if (!ShouldCopy(Path.GetFileName(path))) continue;
            var dest = Path.Combine(destDir, Path.GetFileName(path));
            File.Copy(path, dest, overwrite: true);
        }
    }

    private static void WriteUninstallRegistry(string exePath)
    {
        var installLocation = InstallDirectory;
        var uninstallCmd = $"\"{exePath}\" --uninstall";
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        using var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallRegistryKeyName}",
            writable: true);
        if (key == null) return;

        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", PublisherName);
        key.SetValue("InstallLocation", installLocation);
        key.SetValue("UninstallString", uninstallCmd);
        key.SetValue("QuietUninstallString", uninstallCmd);
        key.SetValue("DisplayIcon", $"\"{exePath}\",0");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        try
        {
            if (Directory.Exists(installLocation))
            {
                var kb = Directory.EnumerateFiles(installLocation, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length) / 1024L;
                if (kb > 0)
                    key.SetValue("EstimatedSize", Math.Max(1, (int)Math.Min(kb, int.MaxValue)),
                        RegistryValueKind.DWord);
            }
        }
        catch { /* optional */ }
    }
}
