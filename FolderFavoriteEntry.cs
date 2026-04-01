namespace ClipboardManager;

/// <summary>Ctrl+G 跳转列表中的收藏路径（与 <see cref="QuickPasteEntry"/> 同形：关键词 + 内容）。</summary>
public class FolderFavoriteEntry
{
    public string Phrase { get; set; } = "";
    public string Path { get; set; } = "";
}
