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
    public string Theme { get; set; } = "System";
    public string PopupPosition { get; set; } = "Caret";
    public double PopupOpacity { get; set; } = 0.95;
    public bool HideOnSameAppClick { get; set; } = true;
    /// <summary>登录 Windows 时自动启动本程序（默认开启）。</summary>
    public bool RunAtStartup { get; set; } = true;
    public int PreviewMaxLines { get; set; } = 2;
    public string PanelModifierKey { get; set; } = "Ctrl";
    public List<QuickPasteEntry> QuickPastes { get; set; } = new();

    public string HotkeyDisplayName
    {
        get
        {
            var parts = new List<string>();
            if ((HotkeyModifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((HotkeyModifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((HotkeyModifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
            if ((HotkeyModifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
            parts.Add(VkToName(HotkeyKey));
            return string.Join("+", parts);
        }
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

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipboardManager");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

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
        Theme = Theme,
        PopupPosition = PopupPosition,
        PopupOpacity = PopupOpacity,
        HideOnSameAppClick = HideOnSameAppClick,
        RunAtStartup = RunAtStartup,
        PreviewMaxLines = PreviewMaxLines,
        PanelModifierKey = PanelModifierKey,
        QuickPastes = QuickPastes.Select(q => new QuickPasteEntry { Phrase = q.Phrase, Content = q.Content }).ToList()
    };
}
