namespace ClipboardManager;

/// <summary>文件对话框跳转菜单中的一项（来源标签 + 目录）。</summary>
public sealed class FileJumpCandidate
{
    public FileJumpCandidate(string label, string path)
    {
        Label = label;
        Path = path;
    }

    public string Label { get; }
    public string Path { get; }
    public string DisplayText => $"{Label}  →  {Path}";
}
