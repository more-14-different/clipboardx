using System.IO;

namespace ClipboardX.Mac;

/// <summary>macOS：数据目录 ~/Library/Application Support/ClipboardX/</summary>
internal static class MacAppPaths
{
    private static string? _dataRoot;

    public static string DataRoot =>
        _dataRoot ?? throw new InvalidOperationException("MacAppPaths.Initialize() has not been called.");

    public static string SettingsFile => Path.Combine(DataRoot, "mac-settings.json");
    public static string SqliteDbPath => Path.Combine(DataRoot, "clipboard_history.db");

    public static void Initialize()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _dataRoot = Path.Combine(baseDir, "ClipboardX");
        Directory.CreateDirectory(_dataRoot);
    }
}
