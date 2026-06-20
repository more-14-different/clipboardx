using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClipboardManager;

public enum EntryType { Text, Image, Files }

public class ClipboardEntry : INotifyPropertyChanged
{
    public EntryType Type { get; set; }
    public string? TextContent { get; set; }
    public byte[]? ImageData { get; set; }
    public string[]? FilePaths { get; set; }

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
    public DateTime CopiedAt { get; set; } = DateTime.Now;
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    public bool IsQuickPaste { get; set; }
    public string? ShortcutPhrase { get; set; }

    /// <summary>SQLite 主键；非持久化条目（如尚未入库或快捷短语）为 null。</summary>
    public long? PersistedId { get; set; }

    /// <summary>粘贴或置顶时更新复制时间并通知列表「时间」列刷新。</summary>
    public void TouchCopiedTime()
    {
        CopiedAt = DateTime.Now;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeAgo)));
    }

    /// <summary>就地修改 <see cref="TextContent"/> 后通知列表预览与检索绑定刷新。</summary>
    public void RaiseTextDisplayPropertiesChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchableText)));
    }

    private bool _isPendingDelete;
    /// <summary>Del 第一次按下时为 true，表示待二次确认删除（删除线提示）。</summary>
    public bool IsPendingDelete
    {
        get => _isPendingDelete;
        set
        {
            if (_isPendingDelete == value) return;
            _isPendingDelete = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPendingDelete)));
        }
    }

    private int _displayIndex;
    public int DisplayIndex
    {
        get => _displayIndex;
        set
        {
            if (_displayIndex == value) return;
            _displayIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndexLabel)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static int PreviewMaxLines { get; set; } = 2;

    public string IndexLabel => DisplayIndex >= 1 && DisplayIndex <= 9 ? DisplayIndex.ToString() : "";

    private int _batchOrder;
    /// <summary>批量粘贴队列中的顺序（1 起）；0 表示不在队列。</summary>
    public int BatchOrder
    {
        get => _batchOrder;
        set
        {
            if (_batchOrder == value) return;
            _batchOrder = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatchOrder)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBatchOrder)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatchOrderLabel)));
        }
    }

    public bool HasBatchOrder => _batchOrder > 0;
    public string BatchOrderLabel => _batchOrder > 0 ? _batchOrder.ToString() : "";

    private BitmapSource? _thumbnail;

    public BitmapSource? Thumbnail
    {
        get
        {
            if (_thumbnail == null)
            {
                if (ImageData != null) _thumbnail = CreateThumbnail();
                else if (IsImageFile) _thumbnail = CreateFileThumbnail();
            }
            return _thumbnail;
        }
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico"
    };

    public bool IsImageFile => Type == EntryType.Files
        && FilePaths is { Length: >= 1 }
        && ImageExtensions.Contains(Path.GetExtension(FilePaths[0]));

    public bool HasThumbnail => Type == EntryType.Image || IsImageFile;
    public bool HasIcon => !HasThumbnail;

    public string TypeIcon => Type switch
    {
        EntryType.Text => IsQuickPaste ? "⚡" : "📝",
        EntryType.Image => "🖼️",
        EntryType.Files => IsImageFile ? "🖼️" : "📁",
        _ => ""
    };

    public string Preview => Type switch
    {
        EntryType.Text => TruncateText(TextContent, PreviewMaxLines, 200),
        EntryType.Image => $"{ImageWidth}×{ImageHeight} 图片",
        EntryType.Files => FormatFilePaths(),
        _ => ""
    };

    public string? SubInfo
    {
        get
        {
            if (ShortcutPhrase != null) return $"⚡ {ShortcutPhrase}";
            if (Type == EntryType.Files && FilePaths is { Length: > 1 }) return $"{FilePaths.Length} 个文件";
            return null;
        }
    }

    public bool HasSubInfo => SubInfo != null;

    public string SearchableText
    {
        get
        {
            var baseText = Type switch
            {
                EntryType.Text => TextContent ?? "",
                EntryType.Files => string.Join(" ", FilePaths?.Select(Path.GetFileName) ?? []),
                EntryType.Image => $"image 图片 {ImageWidth}x{ImageHeight}",
                _ => ""
            };
            return ShortcutPhrase != null ? $"{ShortcutPhrase} {baseText}" : baseText;
        }
    }

    /// <summary>
    /// 全拼与首字母拼接后的检索串（小写、无空格），用于 QuickPaste 内存检索。
    /// 普通历史的拼音检索已下沉到 SQLite FTS5。
    /// </summary>
    public string PinyinSearchBlob => PinyinSearchIndex.BuildBlob(SearchableText);

    public bool MatchesSearch(string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (SearchableText.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var py = PinyinSearchBlob;
        return py.Length > 0 && py.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public string TimeAgo
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

    public Visibility IconVisibility => HasIcon ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbnailVisibility => HasThumbnail ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SubInfoVisibility => HasSubInfo ? Visibility.Visible : Visibility.Collapsed;

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

    private BitmapSource? CreateThumbnail()
    {
        try
        {
            using var ms = new MemoryStream(ImageData!);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = ms;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 64;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private BitmapSource? CreateFileThumbnail()
    {
        try
        {
            var path = FilePaths![0];
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 64;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    public static byte[]? EncodeToPng(BitmapSource image)
    {
        try
        {
            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
