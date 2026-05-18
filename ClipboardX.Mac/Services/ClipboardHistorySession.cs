using System.Collections.ObjectModel;
using ClipboardX.Core;

namespace ClipboardX.Mac;

public sealed class ClipboardHistorySession
{
    private readonly ClipboardHistoryStore _store;
    private readonly ObservableCollection<HistoryEntry> _allItems = new();
    private int _maxItems;

    /// <summary>轮询侧：本应用写入剪贴板后的若干 tick 内跳过采样，避免回灌。</summary>
    public int IgnorePollTicks { get; set; }

    /// <summary>上一次已成功入库条目的指纹，用于跳过完全相同内容的重复轮询。</summary>
    private string? _lastFingerprint;

    public ClipboardHistorySession(ClipboardHistoryStore store, int maxItems)
    {
        _store = store;
        _maxItems = Math.Max(1, maxItems);
        Items = new ReadOnlyObservableCollection<HistoryEntry>(_allItems);
    }

    public ReadOnlyObservableCollection<HistoryEntry> Items { get; }

    public void SetMaxItems(int maxItems)
    {
        _maxItems = Math.Max(1, maxItems);
        TrimUiAndDbToCap();
    }

    public void LoadFromStore()
    {
        _allItems.Clear();
        foreach (var e in _store.LoadNewestFirst(_maxItems))
            _allItems.Add(e);
        UpdateFingerprintFromHead();
    }

    private void UpdateFingerprintFromHead()
    {
        _lastFingerprint = _allItems.Count > 0 ? ClipboardCapture.Fingerprint(_allItems[0]) : null;
    }

    /// <returns>是否有新条目入库</returns>
    public bool TryRecordCaptured(HistoryEntry entry)
    {
        var fp = ClipboardCapture.Fingerprint(entry);
        if (_lastFingerprint != null && fp == _lastFingerprint)
            return false;

        switch (entry.Type)
        {
            case EntryType.Text:
                if (!string.IsNullOrWhiteSpace(entry.TextContent))
                    DeduplicateText(entry.TextContent!);
                break;
            case EntryType.Files:
                if (entry.FilePaths is { Length: > 0 })
                    DeduplicateFiles(entry.FilePaths);
                break;
            case EntryType.Image:
                if (entry.ImageData is { Length: > 0 })
                    DeduplicateImage(entry.ImageData);
                break;
        }

        _store.TryInsert(entry);
        _allItems.Insert(0, entry);
        TrimUiAndDbToCap();
        _lastFingerprint = fp;
        return true;
    }

    private void DeduplicateText(string text)
    {
        var victims = _allItems.Where(x => x.Type == EntryType.Text && x.TextContent == text).ToList();
        foreach (var x in victims)
        {
            _store.TryDelete(x.PersistedId);
            _allItems.Remove(x);
        }
    }

    private void DeduplicateFiles(string[] paths)
    {
        var key = string.Join("|", paths);
        var victims = _allItems.Where(x =>
            x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key).ToList();
        foreach (var x in victims)
        {
            _store.TryDelete(x.PersistedId);
            _allItems.Remove(x);
        }
    }

    private void DeduplicateImage(byte[] pngData)
    {
        var hex = HistoryEntry.ComputeImageBytesMd5Hex(pngData);
        if (hex.Length == 0) return;
        var victims = _allItems.Where(x =>
            x.Type == EntryType.Image && x.ImageContentMd5Hex == hex).ToList();
        foreach (var x in victims)
        {
            _store.TryDelete(x.PersistedId);
            _allItems.Remove(x);
        }
    }

    private void TrimUiAndDbToCap()
    {
        while (_allItems.Count > _maxItems)
        {
            var last = _allItems[^1];
            _store.TryDelete(last.PersistedId);
            _allItems.RemoveAt(_allItems.Count - 1);
        }
        _store.PruneExcess(_maxItems);
    }

    public void Remove(HistoryEntry entry)
    {
        _store.TryDelete(entry.PersistedId);
        _allItems.Remove(entry);
        UpdateFingerprintFromHead();
    }

    public void ClearAll()
    {
        _store.DeleteAll();
        _allItems.Clear();
        _lastFingerprint = null;
    }

    public void TouchReuse(HistoryEntry entry)
    {
        entry.CopiedAt = DateTime.Now;
        if (entry.PersistedId is long id && id > 0)
            _store.TryUpdateCopiedAt(id, entry.CopiedAt);

        _allItems.Remove(entry);
        _allItems.Insert(0, entry);
        UpdateFingerprintFromHead();
    }
}
