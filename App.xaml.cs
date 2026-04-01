using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Application = System.Windows.Application;

namespace ClipboardManager;

public partial class App : Application
{
    private static Mutex? _mutex;
    private WinForms.NotifyIcon? _trayIcon;
    private PopupWindow? _popup;
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WinForms 托盘/上下文菜单在 WPF 宿主中更稳妥
        WinForms.Application.EnableVisualStyles();
        WinForms.Application.SetCompatibleTextRenderingDefault(false);

        if (PerUserInstall.TryProcessUninstallArgs(e.Args))
        {
            Shutdown();
            return;
        }

        if (PerUserInstall.TryInstallToUserProgramsAndRelaunch(e.Args))
        {
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, "ClipboardX_F7A2E9B0", out bool isNew);
        if (!isNew)
        {
#if DEBUG
            try { Console.WriteLine("ClipboardX 已在运行中（互斥锁），本进程将退出。请查看托盘或结束旧进程。"); } catch { }
#endif
            System.Windows.MessageBox.Show("ClipboardX 已在运行中", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        ThemeManager.Apply(_settings.Theme);
        PerUserInstall.EnsureUninstallRegistrationIfNeeded();
        StartupRegistration.Apply(_settings.RunAtStartup);

        _popup = new PopupWindow();
        _popup.Initialize(_settings);
        _popup.SettingsRequested += OpenSettings;

        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = $"ClipboardX ({_settings.HotkeyDisplayName})",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"显示 ({_settings.HotkeyDisplayName})", null, (_, _) =>
            Dispatcher.Invoke(() => _popup?.TogglePopup()));
        menu.Items.Add("设置", null, (_, _) =>
            Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("关于", null, (_, _) =>
            Dispatcher.Invoke(ShowAboutDialog));
        menu.Items.Add("检查更新…", null, (_, _) =>
            _ = CheckForUpdatesAsync());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("卸载…", null, (_, _) =>
            Dispatcher.Invoke(PerUserInstall.PromptUninstallFromTray));
        menu.Items.Add("退出", null, (_, _) =>
            Dispatcher.Invoke(Shutdown));
        _trayIcon.ContextMenuStrip = menu;

        _trayIcon.DoubleClick += (_, _) =>
            Dispatcher.Invoke(() => _popup?.TogglePopup());

#if DEBUG
        try
        {
            Console.WriteLine("ClipboardX 已在运行。");
            Console.WriteLine("- dotnet run 时任务管理器里进程名多为「dotnet」，属于正常情况。");
            Console.WriteLine("- 图标在系统托盘：任务栏右侧「显示隐藏的图标」展开查找（蓝色剪贴板样式）。");
            Console.WriteLine("- 若已有一份在跑，再运行会弹窗提示「已在运行中」。");
            Console.WriteLine("按 Ctrl+C 可结束本控制台（会结束当前这次调试进程）。");
        }
        catch { /* 无控制台时忽略 */ }
#endif
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon != null)
            _trayIcon.Text = $"ClipboardX ({_settings.HotkeyDisplayName})";
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            GitHubUpdateService.LatestReleaseInfo info;
            try
            {
                info = await GitHubUpdateService.FetchLatestReleaseAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"无法获取更新信息（请检查网络或稍后重试）：\n{ex.Message}",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var current = AppInfo.DisplayVersion;
            if (!GitHubUpdateService.IsRemoteNewerThanCurrent(info.TagName, current))
            {
                System.Windows.MessageBox.Show(
                    $"当前已是最新版本（{current}）。",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var asset = info.ChosenAsset;
            var note = GitHubUpdateService.TruncateNote(info.Body);
            var verRemote = info.TagName.TrimStart('v', 'V');
            var installDir = PerUserInstall.GetUpdateInstallDirectory();
            var pkgHint = asset.IsNoRuntimeVariant
                ? "已按当前运行方式匹配：**框架依赖**（与本机 dotnet 共享运行时，包较小）。"
                : "已按当前运行方式匹配：**自带运行时**（单文件内含 .NET，包较大）。";
            var prompt =
                $"发现新版本 {verRemote}（当前 {current}）。\n\n" +
                (note.Length > 0 ? $"说明：{note}\n\n" : "") +
                pkgHint + "\n\n" +
                $"将下载：{asset.Name}\n大小约 {GitHubUpdateService.FormatSizeMb(asset.Size)}\n\n" +
                $"安装目录：\n{installDir}\n\n" +
                "程序将关闭后自动替换并重新启动。\n是否继续？";

            if (System.Windows.MessageBox.Show(
                    prompt,
                    "ClipboardX 更新",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var staging = Path.Combine(Path.GetTempPath(), "ClipboardX-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var zipPath = Path.Combine(staging, "release.zip");
            var extractDir = Path.Combine(staging, "extract");
            var ps1 = Path.Combine(staging, "apply.ps1");
            var updateLaunched = false;
            try
            {
                await GitHubUpdateService.RunWithMarqueeAsync(
                    "正在从 GitHub 下载更新…",
                    () => GitHubUpdateService.DownloadToFileAsync(asset.DownloadUrl, zipPath));

                GitHubUpdateService.ExtractZipToDirectory(zipPath, extractDir);

                if (!File.Exists(Path.Combine(extractDir, "ClipboardX.exe")))
                {
                    System.Windows.MessageBox.Show(
                        "压缩包内未找到 ClipboardX.exe，已中止。",
                        "更新",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (System.Windows.MessageBox.Show(
                        "下载完成。是否立即退出并完成安装？（将自动重启 ClipboardX）",
                        "更新",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                GitHubUpdateService.LaunchDeferredReplaceAndRestart(extractDir, installDir, staging, ps1);
                updateLaunched = true;
                Shutdown();
            }
            finally
            {
                if (!updateLaunched && Directory.Exists(staging))
                    GitHubUpdateService.TryDeleteDirectory(staging);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"更新未成功：\n{ex.Message}",
                "检查更新",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void ShowAboutDialog()
    {
        var body =
            $"版本：{AppInfo.DisplayVersion}\n" +
            $"GitHub：{AppInfo.GitHubUrl}\n\n" +
            "作者：mact\n" +
            "邮箱：chaoji000010@163.com";
        System.Windows.MessageBox.Show(
            body,
            "关于 ClipboardX",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenSettings()
    {
        var copy = _settings.ShallowCopy();
        var window = new SettingsWindow(copy);
        window.ClearHistoryRequested += () => _popup?.ClearHistory();
        window.ShowDialog();

        if (window.DialogResult == true)
        {
            _popup?.ApplySettings(copy);
            _settings.MaxItems = copy.MaxItems;
            _settings.HotkeyModifiers = copy.HotkeyModifiers;
            _settings.HotkeyKey = copy.HotkeyKey;
            _settings.FileJumpHotkeyModifiers = copy.FileJumpHotkeyModifiers;
            _settings.FileJumpHotkeyKey = copy.FileJumpHotkeyKey;
            _settings.FileJumpPickerShowDelayMs = copy.FileJumpPickerShowDelayMs;
            _settings.Theme = copy.Theme;
            _settings.PopupPosition = copy.PopupPosition;
            _settings.PopupOpacity = copy.PopupOpacity;
            _settings.HideOnSameAppClick = copy.HideOnSameAppClick;
            _settings.RunAtStartup = copy.RunAtStartup;
            _settings.PreviewMaxLines = copy.PreviewMaxLines;
            _settings.PanelModifierKey = copy.PanelModifierKey;
            StartupRegistration.Apply(_settings.RunAtStartup);
            _settings.Save();
            UpdateTrayTooltip();
        }
    }

    /// <summary>与托盘相同图稿，用于 WPF 窗口标题栏。</summary>
    public static ImageSource GetWindowIconSource()
    {
        using var icon = CreateTrayIcon();
        return Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    private static Drawing.Icon CreateTrayIcon() => TrayIconSvg.CreateIcon(32);

    protected override void OnExit(ExitEventArgs e)
    {
        _popup?.Cleanup();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
