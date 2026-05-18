using System.IO;
using System.Text.Json;

namespace ClipboardX.Mac;

public sealed class MacSettings
{
    public int MaxHistoryItems { get; set; } = 2000;
    public int PollIntervalMs { get; set; } = 400;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static MacSettings Load()
    {
        try
        {
            var path = MacAppPaths.SettingsFile;
            if (!File.Exists(path))
                return new MacSettings();
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<MacSettings>(json, JsonOpts);
            return s ?? new MacSettings();
        }
        catch
        {
            return new MacSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(MacAppPaths.SettingsFile, json);
        }
        catch
        {
            /* ignore */
        }
    }
}
