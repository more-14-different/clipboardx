using System.Security.Cryptography;

namespace ClipboardX.Core;

public enum EntryType
{
    Text,
    Image,
    Files
}

/// <summary>
/// 剪贴板历史条目（持久化与逻辑层，不含 UI 框架）。
/// </summary>
public sealed class HistoryEntry
{
    public EntryType Type { get; set; }
    public string? TextContent { get; set; }
    public byte[]? ImageData { get; set; }
    public string[]? FilePaths { get; set; }
    public DateTime CopiedAt { get; set; } = DateTime.Now;
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    /// <summary>SQLite 主键；尚未入库为 null。</summary>
    public long? PersistedId { get; set; }

    private string? _imageMd5Hex;

    /// <summary>PNG 图像字节的 MD5（小写十六进制），惰性计算；用于历史项去重。</summary>
    public string? ImageContentMd5Hex
    {
        get
        {
            if (Type != EntryType.Image || ImageData is not { Length: > 0 }) return null;
            if (_imageMd5Hex != null) return _imageMd5Hex;
            _imageMd5Hex = ComputeImageBytesMd5Hex(ImageData);
            return _imageMd5Hex;
        }
    }

    public static string ComputeImageBytesMd5Hex(byte[] data)
    {
        if (data == null || data.Length == 0) return "";
        using var md5 = MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(data)).ToLowerInvariant();
    }

    public string GetSearchableText()
    {
        var baseText = Type switch
        {
            EntryType.Text => TextContent ?? "",
            EntryType.Files => string.Join(" ", FilePaths?.Select(Path.GetFileName) ?? []),
            EntryType.Image => $"image 图片 {ImageWidth}x{ImageHeight}",
            _ => ""
        };
        return baseText;
    }

    public string GetPreview(int previewMaxLines = 2)
    {
        return Type switch
        {
            EntryType.Text => TruncateText(TextContent, previewMaxLines, 200),
            EntryType.Image => $"{ImageWidth}×{ImageHeight} 图片",
            EntryType.Files => FormatFilePaths(),
            _ => ""
        };
    }

    /// <summary>列表展示用（与 Windows 版 TimeAgo 语义一致）。</summary>
    public string TimeAgoDisplay
    {
        get
        {
            var span = DateTime.Now - CopiedAt;
            if (span.TotalSeconds < 60) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}分钟前";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}小时前";
            return CopiedAt.ToString("MM-dd HH:mm");
        }
    }

    public string TypeEmoji => Type switch
    {
        EntryType.Text => "📝",
        EntryType.Image => "🖼️",
        EntryType.Files => "📁",
        _ => ""
    };

    /// <summary>列表主行预览。</summary>
    public string PreviewLine => GetPreview(2);

    public bool MatchesSearch(string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        var searchable = GetSearchableText();
        if (searchable.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var py = PinyinSearchIndex.BuildBlob(searchable);
        return py.Length > 0 && py.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateText(string? text, int maxLines, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Split('\n');
        var taken = lines.Take(maxLines).Select(l => l.TrimEnd('\r'));
        var result = string.Join("\n", taken);
        if (result.Length > maxChars)
            result = result[..maxChars] + "…";
        else if (lines.Length > maxLines)
            result += " …";
        return result;
    }

    private string FormatFilePaths()
    {
        if (FilePaths == null || FilePaths.Length == 0) return "";
        var names = FilePaths.Select(Path.GetFileName).Take(3);
        var result = string.Join(", ", names);
        if (FilePaths.Length > 3) result += $" (+{FilePaths.Length - 3})";
        return result;
    }
}
