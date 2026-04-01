using System.ComponentModel;
using System.IO;
using System.Windows;

namespace ClipboardManager;

/// <summary>跳转列表中的一行（动态路径 + 收藏），支持检索拼音与快捷标号。</summary>
public sealed class FileJumpPickerRow : INotifyPropertyChanged
{
    public FileJumpPickerRow(string sourceLabel, string path, bool isFavorite, string? phrase = null)
    {
        SourceLabel = sourceLabel;
        Path = path;
        IsFavorite = isFavorite;
        Phrase = phrase ?? "";
    }

    public string SourceLabel { get; }
    public string Path { get; }
    public bool IsFavorite { get; }
    public string Phrase { get; }

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

    public string IndexLabel => DisplayIndex is >= 1 and <= 9 ? DisplayIndex.ToString() : "";

    public string TypeIcon => IsFavorite ? "⭐" : "📁";

    public string PreviewLine => IsFavorite && !string.IsNullOrEmpty(Phrase)
        ? $"{Phrase}  ·  {SourceLabel}"
        : SourceLabel;

    public string PathLine => Path;

    public string? SubInfo => IsFavorite && !string.IsNullOrEmpty(Phrase) ? $"⚡ {Phrase}" : null;

    public Visibility SubInfoVisibility => string.IsNullOrEmpty(SubInfo) ? Visibility.Collapsed : Visibility.Visible;

    public string SearchablePrimary =>
        $"{Phrase} {SourceLabel} {Path}";

    private string? _pinyinCacheKey;
    private string? _pinyinBlob;

    public string PinyinSearchBlob
    {
        get
        {
            var key = SearchablePrimary;
            if (_pinyinBlob != null && string.Equals(_pinyinCacheKey, key, StringComparison.Ordinal))
                return _pinyinBlob;
            _pinyinCacheKey = key;
            _pinyinBlob = PinyinSearchIndex.BuildBlob(key);
            return _pinyinBlob;
        }
    }

    public bool MatchesSearch(string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (SearchablePrimary.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var py = PinyinSearchBlob;
        return py.Length > 0 && py.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
