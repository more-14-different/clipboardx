using System.Linq;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using ClipboardX.Core;

namespace ClipboardX.Mac;

internal static class ClipboardCapture
{
    /// <summary>按与 Windows 监控相近的优先级尝试抓取剪贴板。</summary>
    public static async Task<HistoryEntry?> TryCaptureAsync(IClipboard clipboard)
    {
        try
        {
            var formats = await clipboard.GetFormatsAsync();
            if (formats == null || !formats.Any())
                return null;

            // 文件列表优先
            if (formats.Contains(DataFormats.Files))
            {
                var data = await clipboard.GetDataAsync(DataFormats.Files);
                if (data is IEnumerable<IStorageItem> items)
                {
                    var paths = new List<string>();
                    foreach (var it in items)
                    {
                        var p = TryGetPath(it);
                        if (!string.IsNullOrEmpty(p))
                            paths.Add(p);
                    }
                    if (paths.Count > 0)
                        return new HistoryEntry { Type = EntryType.Files, FilePaths = paths.ToArray(), CopiedAt = DateTime.Now };
                }
            }

            if (formats.Contains(DataFormats.Text))
            {
                var text = await clipboard.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                    return new HistoryEntry { Type = EntryType.Text, TextContent = text, CopiedAt = DateTime.Now };
            }

            // 常见位图 / PNG 载荷（平台实现不一，逐个尝试；DataFormats 无 Bitmap 常量，用字面量）
            foreach (var fmt in new[] { "Bitmap", "PNG", "public.png", "image/png", "image/tiff" })
            {
                if (!formats.Contains(fmt)) continue;
                var raw = await clipboard.GetDataAsync(fmt);
                var png = TryGetPngBytes(raw);
                if (png?.Length > 0)
                    return new HistoryEntry
                    {
                        Type = EntryType.Image,
                        ImageData = png,
                        ImageWidth = 0,
                        ImageHeight = 0,
                        CopiedAt = DateTime.Now
                    };
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    private static string? TryGetPath(IStorageItem item)
    {
        try
        {
            return item.TryGetLocalPath();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryGetPngBytes(object? raw)
    {
        try
        {
            switch (raw)
            {
                case byte[] bytes when bytes.Length > 0:
                    return bytes;
                case Stream s:
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    public static string Fingerprint(HistoryEntry e)
    {
        return e.Type switch
        {
            EntryType.Text => "t:" + (e.TextContent ?? ""),
            EntryType.Files => "f:" + string.Join("|", e.FilePaths ?? Array.Empty<string>()),
            EntryType.Image => "i:" + HistoryEntry.ComputeImageBytesMd5Hex(e.ImageData ?? Array.Empty<byte>()),
            _ => ""
        };
    }
}
