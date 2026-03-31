using System.Windows;
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

        _mutex = new Mutex(true, "ClipboardManager_F7A2E9B0", out bool isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show("剪切板管理器已在运行中", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        ThemeManager.Apply(_settings.Theme);

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
        menu.Items.Add("退出", null, (_, _) =>
            Dispatcher.Invoke(Shutdown));
        _trayIcon.ContextMenuStrip = menu;

        _trayIcon.DoubleClick += (_, _) =>
            Dispatcher.Invoke(() => _popup?.TogglePopup());
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
            _settings.PreviewMaxLines = copy.PreviewMaxLines;
            _settings.PanelModifierKey = copy.PanelModifierKey;
            _settings.Save();
            UpdateTrayTooltip();
        }
    }

    private static Drawing.Icon CreateTrayIcon()
    {
        var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var boardBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(137, 180, 250));
            g.FillRectangle(boardBrush, 5, 5, 22, 26);
            using var clipBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(205, 214, 244));
            g.FillRectangle(clipBrush, 10, 1, 12, 8);
            using var lineBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(30, 30, 46));
            g.FillRectangle(lineBrush, 9, 14, 14, 2);
            g.FillRectangle(lineBrush, 9, 19, 14, 2);
            g.FillRectangle(lineBrush, 9, 24, 10, 2);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

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
