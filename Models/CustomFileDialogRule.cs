using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace ClipboardManager;

/// <summary>
/// 用户手动添加的「未内置识别」文件对话框匹配规则，以及跳转策略顺序/探测结果。
/// </summary>
public sealed class CustomFileDialogRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>仅作备忘，不参与匹配。</summary>
    public string Note { get; set; } = "";

    /// <summary>进程主模块基名（小写、无扩展名）；空表示不限制进程。</summary>
    public string ProcessExeBase { get; set; } = "";

    /// <summary>顶层窗口类名（与 Win32 一致，区分大小写按 OrdinalIgnoreCase 比较）。</summary>
    public string WindowClass { get; set; } = "";

    /// <summary>窗口标题须包含的子串；空表示不限制标题。</summary>
    public string TitleContains { get; set; } = "";

    /// <summary>未锁定策略时依次尝试的策略 id（见 <see cref="CustomFileDialogStore"/> 常量）。</summary>
    public List<string> StrategyOrder { get; set; } = new();

    /// <summary>探测或上次成功的策略 id，跳转时优先尝试。</summary>
    public string? PinnedStrategy { get; set; }

    [JsonIgnore]
    public string SummaryLine
    {
        get
        {
            var proc = string.IsNullOrEmpty(ProcessExeBase) ? "*" : ProcessExeBase;
            var pin = string.IsNullOrEmpty(PinnedStrategy) ? "自动" : PinnedStrategy;
            return $"{WindowClass}  @ {proc}  → {pin}";
        }
    }

    public static CustomFileDialogRule CreateFromWindow(IntPtr hwnd)
    {
        var windowClass = Win32.GetWindowClassName(hwnd);
        var title = Win32.GetWindowText(hwnd);
        Win32.GetWindowThreadProcessId(hwnd, out var pid);

        var exeBase = "";
        if (pid != 0)
        {
            var hProc = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    if (Win32.GetModuleFileNameEx(hProc, IntPtr.Zero, sb, sb.Capacity) > 0)
                        exeBase = Path.GetFileNameWithoutExtension(sb.ToString()).ToLowerInvariant();
                }
                finally { Win32.CloseHandle(hProc); }
            }
        }

        return new CustomFileDialogRule
        {
            WindowClass = windowClass,
            ProcessExeBase = exeBase,
            TitleContains = "",
            Note = string.IsNullOrEmpty(title) ? "" : title,
        };
    }
}
