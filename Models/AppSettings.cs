using System.IO;
using System.Text.Json;

namespace ClipboardManager;

public class QuickPasteEntry
{
    public string Phrase { get; set; } = "";
    public string Content { get; set; } = "";
}

public class AppSettings
{
    public int MaxItems { get; set; } = 2000;
    public uint HotkeyModifiers { get; set; } = Win32.MOD_CONTROL;
    public uint HotkeyKey { get; set; } = Win32.VK_OEM_3;

    /// <summary>「打开/保存」对话框中跳转到文件夹的全局快捷键（默认 Ctrl+G）。</summary>
    public uint FileJumpHotkeyModifiers { get; set; } = Win32.MOD_CONTROL;
    public uint FileJumpHotkeyKey { get; set; } = Win32.VK_G;

    /// <summary>多候选时跳转列表弹出前的延时（毫秒）；0 表示立即弹出。</summary>
    public int FileJumpPickerShowDelayMs { get; set; } = 500;

    /// <summary>
    /// 系统公共文件对话框内跳转时，是否尝试将 Shell 导航 DLL 注入宿主进程（IShellBrowser::BrowseObject）。
    /// 关闭后仅走地址栏/键入模拟，兼容部分杀软或宿主拦截注入的环境；WPS 等自定义对话框始终不注入。
    /// </summary>
    public bool EnableShellNavigateInject { get; set; } = true;
    public string Theme { get; set; } = "System";
    public string PopupPosition { get; set; } = "Caret";
    public double PopupOpacity { get; set; } = 0.95;
    public bool HideOnSameAppClick { get; set; } = true;
    /// <summary>登录 Windows 时自动启动本程序（默认开启）。</summary>
    public bool RunAtStartup { get; set; } = true;
    public int PreviewMaxLines { get; set; } = 2;
    public string PanelModifierKey { get; set; } = "Ctrl";
    public List<QuickPasteEntry> QuickPastes { get; set; } = new();

    /// <summary>Ctrl+G 跳转列表顶部展示的收藏目录（Phrase 为关键词/别名，供检索）。</summary>
    public List<FolderFavoriteEntry> FolderFavorites { get; set; } = new();

    /// <summary>最近一次在「打开/保存」对话框中记录到的文件夹，供 Ctrl+G 跳转。</summary>
    public string LastFileDialogFolder { get; set; } = "";

    public string HotkeyDisplayName => FormatHotkey(HotkeyModifiers, HotkeyKey);

    public string FileJumpHotkeyDisplayName => FormatHotkey(FileJumpHotkeyModifiers, FileJumpHotkeyKey);

    public static string FormatHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(VkToName(key));
        return string.Join("+", parts);
    }

    private static string VkToName(uint vk) => vk switch
    {
        0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]",
        0xDC => "\\", 0xBA => ";", 0xDE => "'", 0xBC => ",", 0xBE => ".",
        0xBF => "/", 0x20 => "Space",
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"0x{vk:X2}"
    };

    private static readonly string LegacySettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipboardManager");

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipboardX");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");
    private static readonly string LegacySettingsFile = Path.Combine(LegacySettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                using (var doc = JsonDocument.Parse(json))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
                    // 旧版 settings.json 无此字段时 Json 反序列化为 false，产品默认应为开启
                    if (!doc.RootElement.TryGetProperty(nameof(RunAtStartup), out _))
                        settings.RunAtStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(EnableShellNavigateInject), out _))
                        settings.EnableShellNavigateInject = true;
                    if (settings.FolderFavorites == null)
                        settings.FolderFavorites = new List<FolderFavoriteEntry>();
                    return settings;
                }
            }

            if (File.Exists(LegacySettingsFile))
            {
                var json = File.ReadAllText(LegacySettingsFile);
                using (var doc = JsonDocument.Parse(json))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
                    if (!doc.RootElement.TryGetProperty(nameof(RunAtStartup), out _))
                        settings.RunAtStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(EnableShellNavigateInject), out _))
                        settings.EnableShellNavigateInject = true;
                    if (settings.FolderFavorites == null)
                        settings.FolderFavorites = new List<FolderFavoriteEntry>();
                    settings.Save();
                    return settings;
                }
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    public AppSettings ShallowCopy() => new()
    {
        MaxItems = MaxItems,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyKey = HotkeyKey,
        FileJumpHotkeyModifiers = FileJumpHotkeyModifiers,
        FileJumpHotkeyKey = FileJumpHotkeyKey,
        FileJumpPickerShowDelayMs = FileJumpPickerShowDelayMs,
        EnableShellNavigateInject = EnableShellNavigateInject,
        Theme = Theme,
        PopupPosition = PopupPosition,
        PopupOpacity = PopupOpacity,
        HideOnSameAppClick = HideOnSameAppClick,
        RunAtStartup = RunAtStartup,
        PreviewMaxLines = PreviewMaxLines,
        PanelModifierKey = PanelModifierKey,
        QuickPastes = QuickPastes.Select(q => new QuickPasteEntry { Phrase = q.Phrase, Content = q.Content }).ToList(),
        FolderFavorites = FolderFavorites.Select(f => new FolderFavoriteEntry { Phrase = f.Phrase, Path = f.Path }).ToList(),
        LastFileDialogFolder = LastFileDialogFolder
    };
}
