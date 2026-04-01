using System.Drawing;
using System.IO;
using System.Text;
using Svg;

namespace ClipboardManager;

/// <summary>
/// 由用户提供的 Iconfont 剪贴板 SVG（viewBox 0 0 1024 1024）光栅化为托盘/窗口图标。
/// 若改路径或配色，请同步更新 <c>tools/GenAppIcon</c> 并重新生成 <c>assets/clipboard.ico</c>（供 exe / 快捷方式嵌入图标）。
/// </summary>
internal static class TrayIconSvg
{
    /// <summary>主色（青绿 #139493）。</summary>
    public const string MainBlueHex = "#139493";

    /// <summary>中间横条：同系浅色，小图可辨。</summary>
    public const string BarBlueHex = "#B5E8E7";

    private const string PathClipboard =
        "M880 192H768c-8.8 0-16-7.2-16-16V64c0-35.4-28.7-64-64-64H144c-35.3 0-64 28.6-64 64v704c0 35.3 28.7 64 64 64h112c8.8 0 16 7.2 16 16v112c0 35.3 28.7 64 64 64h544c35.3 0 64-28.7 64-64V256c0-35.4-28.7-64-64-64z m0 752c0 8.8-7.2 16-16 16H352c-8.8 0-16-7.2-16-16V272c0-8.8 7.2-16 16-16h512c8.8 0 16 7.2 16 16v672z";

    private const string PathBar =
        "M704 352H512c-17.7 0-32-14.3-32-32s14.3-32 32-32h192c17.7 0 32 14.3 32 32s-14.3 32-32 32z";

    public static Icon CreateIcon(int size = 32)
    {
        var svg = $"""
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">
  <path fill="{MainBlueHex}" d="{PathClipboard}"/>
  <path fill="{BarBlueHex}" d="{PathBar}"/>
</svg>
""";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(svg));
        var doc = SvgDocument.Open<SvgDocument>(ms);
        using var bmp = doc.Draw(size, size);
        using var tmp = Icon.FromHandle(bmp.GetHicon());
        return (Icon)tmp.Clone();
    }
}
