using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace ClipboardManager;

public static class ThemeManager
{
    public static void Apply(string theme)
    {
        bool dark = theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => IsSystemDark()
        };
        ApplyColors(dark);
    }

    private static void ApplyColors(bool dark)
    {
        var r = Application.Current.Resources;
        if (dark)
        {
            r["PopupBgBrush"] = B(0xF2, 0x1E, 0x1E, 0x2E);
            r["WindowBgBrush"] = B(0x1E, 0x1E, 0x2E);
            r["SurfaceBrush"] = B(0x31, 0x32, 0x44);
            r["HoverBrush"] = B(0x31, 0x32, 0x44);
            r["SelectedBrush"] = B(0x45, 0x47, 0x5A);
            r["FooterBrush"] = B(0x18, 0x18, 0x25);
            r["PrimaryText"] = B(0xCD, 0xD6, 0xF4);
            r["SecondaryText"] = B(0x93, 0x98, 0xB2);
            r["MutedText"] = B(0x74, 0x78, 0x91);
            r["HintText"] = B(0x9D, 0xA3, 0xBE);
            r["AccentBg"] = B(0x89, 0xB4, 0xFA);
            r["AccentFg"] = B(0x1E, 0x1E, 0x2E);
            r["ThemeBorder"] = B(0x6B, 0x6F, 0x85);
            r["ThemeSeparator"] = B(0x31, 0x32, 0x44);
            r["DangerBg"] = B(0xF3, 0x8B, 0xA8);
            r["ScrollBarTrackBrush"] = B(0x25, 0x26, 0x37);
            r["ScrollBarThumbBrush"] = B(0x6A, 0x6E, 0x88);
            r["ScrollBarThumbHoverBrush"] = B(0x88, 0x8D, 0xA8);
        }
        else
        {
            r["PopupBgBrush"] = B(0xF5, 0xEF, 0xF1, 0xF5);
            r["WindowBgBrush"] = B(0xEF, 0xF1, 0xF5);
            r["SurfaceBrush"] = B(0xE6, 0xE9, 0xEF);
            r["HoverBrush"] = B(0xE6, 0xE9, 0xEF);
            r["SelectedBrush"] = B(0xDC, 0xE0, 0xE8);
            r["FooterBrush"] = B(0xCC, 0xD0, 0xDA);
            r["PrimaryText"] = B(0x4C, 0x4F, 0x69);
            r["SecondaryText"] = B(0x8C, 0x8F, 0xA1);
            r["MutedText"] = B(0x9C, 0xA0, 0xB0);
            r["HintText"] = B(0x7C, 0x7F, 0x93);
            r["AccentBg"] = B(0x1E, 0x66, 0xF5);
            r["AccentFg"] = B(0xFF, 0xFF, 0xFF);
            r["ThemeBorder"] = B(0xBC, 0xC0, 0xCC);
            r["ThemeSeparator"] = B(0xCC, 0xD0, 0xDA);
            r["DangerBg"] = B(0xD2, 0x0F, 0x39);
            r["ScrollBarTrackBrush"] = B(0xE0, 0xE3, 0xEB);
            r["ScrollBarThumbBrush"] = B(0xB4, 0xB8, 0xC8);
            r["ScrollBarThumbHoverBrush"] = B(0x98, 0x9E, 0xB2);
        }
    }

    private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static SolidColorBrush B(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }
}
