using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClipboardX.Core;
using ClipboardX.Mac.Views;

namespace ClipboardX.Mac;

public partial class App : Application
{
    private MacTrayIcon? _tray;
    private GlobalHotKeyService? _hotKey;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MacAppPaths.Initialize();
            var settings = MacSettings.Load();
            var store = new ClipboardHistoryStore(MacAppPaths.SqliteDbPath);
            var session = new ClipboardHistorySession(store, settings.MaxHistoryItems);
            session.LoadFromStore();

            var main = new MainWindow(session, settings)
            {
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen,
                Width = 440,
                Height = 520,
                Title = "ClipboardX"
            };
            desktop.MainWindow = main;

            _hotKey = new GlobalHotKeyService(main.ToggleVisible);
            _hotKey.Start();

            _tray = MacTrayIcon.Create(main, settings);

            desktop.Exit += (_, _) =>
            {
                _hotKey.Dispose();
                _tray?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
