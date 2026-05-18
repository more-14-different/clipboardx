using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using ClipboardX.Mac.Views;

namespace ClipboardX.Mac;

internal sealed class MacTrayIcon : IDisposable
{
    private readonly TrayIcon _tray;

    private MacTrayIcon(TrayIcon tray) => _tray = tray;

    public static MacTrayIcon Create(MainWindow main, MacSettings _)
    {
        var tray = new TrayIcon
        {
            ToolTipText = "ClipboardX",
            IsVisible = true,
            Icon = null,
            Menu = BuildMenu(main)
        };
        tray.Clicked += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(main.ToggleVisible);
        return new MacTrayIcon(tray);
    }

    private static NativeMenu BuildMenu(MainWindow main)
    {
        var show = new NativeMenuItem("显示剪贴板面板");
        show.Click += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(main.ShowFromTray);

        var quit = new NativeMenuItem("退出");
        quit.Click += (_, _) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
                d.Shutdown();
        };

        var menu = new NativeMenu();
        menu.Items.Add(show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);
        return menu;
    }

    public void Dispose() => _tray.Dispose();
}
