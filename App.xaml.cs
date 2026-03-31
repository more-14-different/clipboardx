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

        _mutex = new Mutex(true, "ClipboardManager_F7A2E9B0", out bool isNew);
        if (!isNew)
        {
#if DEBUG
            try { Console.WriteLine("剪切板管理器已在运行中（互斥锁），本进程将退出。请查看托盘或结束旧进程。"); } catch { }
#endif
            System.Windows.MessageBox.Show("剪切板管理器已在运行中", "提示",
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
            Text = $"剪切板管理器 ({_settings.HotkeyDisplayName})",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"显示 ({_settings.HotkeyDisplayName})", null, (_, _) =>
            Dispatcher.Invoke(() => _popup?.TogglePopup()));
        menu.Items.Add("设置", null, (_, _) =>
            Dispatcher.Invoke(OpenSettings));
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
            Console.WriteLine("剪切板管理器已在运行。");
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
            _trayIcon.Text = $"剪切板管理器 ({_settings.HotkeyDisplayName})";
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
