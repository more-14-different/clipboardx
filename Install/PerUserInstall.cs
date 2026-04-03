using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace ClipboardManager;

/// <summary>
/// 将程序安装到当前用户目录（%LocalAppData%\Programs\ClipboardX），并注册“应用和功能”卸载项。
/// </summary>
public static class PerUserInstall
{
    public const string UninstallRegistryKeyName = "ClipboardX";
    private const string PublisherName = "ClipboardX";

    private static readonly string InstallRootRelative =
        Path.Combine("Programs", "ClipboardX");

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            InstallRootRelative);

    public static string InstalledExecutablePath =>
        Path.Combine(InstallDirectory, "ClipboardX.exe");

    public static string DisplayName => "ClipboardX";

    /// <summary>当前用户「开始」菜单程序文件夹中的快捷方式路径。</summary>
    public static string StartMenuShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs",
            $"{DisplayName}.lnk");

    private static bool IsDevBypass =>
        string.Equals(Environment.GetEnvironmentVariable("CLIPBOARD_MANAGER_DEV"),
            "1", StringComparison.Ordinal);

    /// <summary>
    /// 是否允许执行“复制到用户 Program 目录”（正式分发用的 exe）；
    /// <c>dotnet.exe</c> 直接托管 dll 时 ProcessPath 为 dotnet，会跳过。
    /// </summary>
    /// <remarks>
    /// <c>dotnet run</c> 实际会启动 <c>bin\...\ClipboardX.exe</c> apphost，ProcessPath 不是 dotnet；
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

    /// <summary>在线更新时覆盖此目录（与 PerUserInstall 安装目录一致，或为当前便携运行目录）。</summary>
    public static string GetUpdateInstallDirectory()
    {
        if (AppPaths.IsPortable)
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : NormalizePath(dir);
        }
        if (IsRunningFromInstallLocation())
            return InstallDirectory;
        var d = Path.GetDirectoryName(Environment.ProcessPath);
        return string.IsNullOrEmpty(d) ? InstallDirectory : NormalizePath(d);
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
        var ver = AppInfo.DisplayVersion;
        var verify = System.Windows.MessageBox.Show(
            $"将卸载 ClipboardX（版本 {ver}）、移除开始菜单快捷方式、开机启动与「应用和功能」条目，并删除安装目录中的程序文件。\n\n是否同时删除配置与历史记录？（%AppData%\\ClipboardX，旧版可能在 ClipboardManager）\n\n「是」删除程序与配置；「否」只删程序；「取消」中止。",
            $"卸载 ClipboardX — {ver}",
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
            // 删除统一 DataRoot（安装模式为 %LocalAppData%\ClipboardX）
            try
            {
                if (Directory.Exists(AppPaths.DataRoot))
                    Directory.Delete(AppPaths.DataRoot, recursive: true);
            }
            catch { /* ignore */ }

            // 清理可能残存的旧版 Roaming 目录
            foreach (var folder in new[] { "ClipboardX", "ClipboardManager" })
            {
                try
                {
                    var appDataDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        folder);
                    if (Directory.Exists(appDataDir))
                        Directory.Delete(appDataDir, recursive: true);
                }
                catch
                {
                    // ignore
                }
            }
        }

        TryRemoveStartMenuShortcut();

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
        if (AppPaths.IsPortable) return;
        if (!IsRunningFromInstallLocation()) return;
        try
        {
            using var check = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallRegistryKeyName}");
            if (check == null)
                WriteUninstallRegistry(InstalledExecutablePath);
            else
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallRegistryKeyName}",
                        writable: true);
                    key?.SetValue("DisplayVersion", AppInfo.DisplayVersion);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            return;
        }

        EnsureStartMenuShortcut(InstalledExecutablePath);
    }

    /// <summary>
    /// 更新或创建开始菜单中的 ClipboardX 快捷方式（指向已安装的 exe）。
    /// </summary>
    public static void EnsureStartMenuShortcut(string targetExePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(StartMenuShortcutPath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            WriteShellShortcut(
                StartMenuShortcutPath,
                targetExePath,
                Path.GetDirectoryName(targetExePath) ?? InstallDirectory,
                DisplayName,
                $"{targetExePath},0");
        }
        catch
        {
            // 无权写开始菜单或 COM 异常时不阻止主流程
        }
    }

    /// <summary>使用 <c>WScript.Shell</c> 写 .lnk，避免 SDK 项目无法引用 <c>IWshRuntimeLibrary</c>。</summary>
    private static void WriteShellShortcut(string shortcutPath, string targetPath, string workingDirectory,
        string description, string iconLocation)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        object? shell = Activator.CreateInstance(t);
        if (shell == null) return;
        object? shortcut = null;
        try
        {
            shortcut = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            if (shortcut == null) return;
            var st = shortcut.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [workingDirectory]);
            st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, [description]);
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [iconLocation]);
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null)
                Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void TryRemoveStartMenuShortcut()
    {
        try
        {
            if (File.Exists(StartMenuShortcutPath))
                File.Delete(StartMenuShortcutPath);
        }
        catch
        {
            // ignore
        }
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
        if (AppPaths.IsPortable) return false;
        if (!ShouldUsePerUserInstallPipeline()) return false;
        if (IsRunningFromInstallLocation()) return false;

        try
        {
            TryStopProcessesRunningFromInstallDirectory();

            Directory.CreateDirectory(InstallDirectory);
            var source = Environment.ProcessPath!;
            var sourceDir = Path.GetDirectoryName(source)!;

            // 框架依赖：apphost 旁有 ClipboardX.dll，需整套复制；单文件发布仅有大 exe 则无此文件
            if (File.Exists(Path.Combine(sourceDir, "ClipboardX.dll")))
                CopyFrameworkDeploymentFiles(sourceDir, InstallDirectory);
            else
                File.Copy(source, InstalledExecutablePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "未能复制程序到安装目录（可能被杀软拦截、旧进程未退出导致文件被占用、或无权写入）：\n" +
                ex.Message +
                "\n\n可在任务管理器中结束「ClipboardX」后重试，或注销/重启后再试。\n" +
                "将尝试从当前位置继续运行。",
                DisplayName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        WriteUninstallRegistry(InstalledExecutablePath);
        EnsureStartMenuShortcut(InstalledExecutablePath);

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

    /// <summary>
    /// 结束「可执行文件路径等于安装目录下 ClipboardX.exe」的进程，避免覆盖时被 Windows 锁定导致 Access denied。
    /// 典型场景：卸载后托盘进程未退出、或延迟删除尚未完成。
    /// </summary>
    private static void TryStopProcessesRunningFromInstallDirectory()
    {
        var want = NormalizePath(InstalledExecutablePath);
        foreach (var p in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(InstalledExecutablePath)))
        {
            try
            {
                string path;
                try
                {
                    path = p.MainModule?.FileName ?? "";
                }
                catch
                {
                    continue;
                }

                if (!string.Equals(NormalizePath(path), want, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    p.CloseMainWindow();
                }
                catch
                {
                    // 托盘应用常无主窗口
                }

                try
                {
                    if (!p.WaitForExit(2000))
                        p.Kill(entireProcessTree: false);
                }
                catch
                {
                    // 已无权限或已退出
                }
            }
            catch
            {
                // 单进程忽略
            }
            finally
            {
                try
                {
                    p.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        }

        Thread.Sleep(400);
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
        var version = AppInfo.DisplayVersion;

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
